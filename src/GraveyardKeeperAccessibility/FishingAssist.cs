namespace GraveyardKeeperAccessibility;

/// <summary>
/// Makes the game's fishing mini-game playable without sight.
///
/// Fishing (FishingGUI) is a state machine: choose bait → hold E to cast, releasing on an
/// oscillating bar that picks a distance tier → wait for a hidden bite timer → a short reaction
/// window to hook the bite → a real-time "keep the fish inside a moving bar" reel game → take out.
/// The two middle steps (reacting to the bite, tracking the fish) are fast and purely visual, so a
/// blind player can't do them. Bait and distance, however, DO matter — together they select which
/// fish bites (FishingGUI.GetRandomFish) — so we keep those as real choices and just make them
/// audible.
///
/// This module (patches live in this class, dispatched from Plugin.RegisterPatches) does four
/// things, mirroring the CombatAssist approach (narrate + assist the twitch parts, keep the
/// decisions with the player):
///   1. NARRATION — every state change is spoken through the single ChangeState funnel; the
///      selected bait is spoken as it's cycled; and while the cast bar sweeps, the current distance
///      tier and whether any fish live there are spoken live so the player can release on a good one.
///   2. AUTO-CATCH — the instant the fish bites (WaitingForPulling), we force a successful take-out,
///      awarding exactly the fish that bait+distance already selected. No reaction time, no reel
///      tracking. This is the same idea stardew-access uses for Stardew fishing. If the standalone
///      NoTimeForFishing mod is also installed we defer the catch to IT (it patches the same flow)
///      and only narrate — see DeferToNoTimeForFishing — so the two never fight the state machine.
///   3. SONAR — with auto-catch off, the bite and the reel game are played BY EAR instead: a ding
///      the instant the fish bites, then a tone that tracks the fish against your bar (see
///      FishingSonar and FishingGUI_UpdatePulling_Postfix). This is the part stardew-access solves the same way for
///      Stardew's bobber bar, and it is what makes catching a fish by hand possible without sight.
///   4. TOGGLE — Ctrl+F flips auto-catch off, handing the player the real mini-game with the sonar
///      on. Auto-catch stays the default: it is the one mode that cannot be lost, and the manual
///      game is a choice, not a requirement.
///
/// All private FishingGUI state is reached with Harmony's Traverse; everything is wrapped in
/// try/catch so a reflection miss can never wedge the fishing UI.
/// </summary>
internal static class FishingAssist
{
    private static ManualLogSource _log;
    private static bool _enabled = true;   // auto-catch on by default (see class summary)

    // Manual play has two difficulties, and Ctrl+F cycles auto → assisted → full speed → auto.
    //
    // Assisted is not a concession, it is the point: the mini-game was built to be tracked with the
    // eyes, and the fastest fish in the game move their marker across a third of the column in well
    // under a second. Hearing where the fish is does not make that reachable — the first player to
    // try it got the cues, understood them, and still could not land the bar in time. Halving the
    // fish's speed leaves the shape of the task, the controls and the catch requirement exactly as
    // they are and gives the ears the time the eyes did not need. Full speed stays one keypress away
    // for anyone who wants the unmodified fight.
    private static bool _assisted = true;

    // NoTimeForFishing (p1xel8ted.gyk.notimeforfishing) is a separate mod that also auto-completes
    // fishing — it patches the same FishingGUI flow methods. If it's installed we let IT drive the
    // catch and only NARRATE (bait, distance, state, "Caught X"), so the two don't fight over the
    // state machine (double speech, bait double-consume). Our own auto-catch takes over only when it
    // isn't present. Resolved lazily on first use, not at Init, so plugin load order can't matter.
    private const string NoTimeForFishingGuid = "p1xel8ted.gyk.notimeforfishing";
    private static bool? _deferCache;
    private static bool DeferToNoTimeForFishing
    {
        get
        {
            if (_deferCache.HasValue) return _deferCache.Value;
            bool present = false;
            try { present = Chainloader.PluginInfos != null && Chainloader.PluginInfos.ContainsKey(NoTimeForFishingGuid); }
            catch { }
            _deferCache = present;
            _log?.LogInfo($"[FISHING] NoTimeForFishing detected: {present}. Auto-catch {(present ? "deferred to it" : "handled here")}.");
            return present;
        }
    }

    // Transition tracking for the ChangeState narrator: the state we last saw entered. Lets us tell
    // a fresh cast-bar bounce-back (DistanceChoosing → BaitChoosing, "no fish there") apart from a
    // brand-new fishing session opening (anything-else → BaitChoosing, the intro instructions).
    private static FishingGUI.FishingState _lastState = FishingGUI.FishingState.None;

    // Live cast-distance narration: the tier index (0/1/2) we last spoke while the bar swept, so we
    // only speak when it actually changes rather than every frame. Reset to -1 on entering the bar.
    private static int _lastAnnouncedTier = -1;

    // The full "here is what the sounds mean" briefing is spoken the first time a player enters
    // manual mode in a session (and again whenever they switch back to it with Ctrl+F), then a one
    // line reminder on every later cast — it would otherwise be read out before every single throw.
    private static bool _manualIntroSpoken;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        FishingSonar.Init(log);
        _log?.LogInfo("[FISHING] FishingAssist initialized (auto-catch on, Ctrl+F toggles)");
    }

    // ── Toggle (polled every frame the fishing UI is open) ──────────────────────────────────────

    internal static void FishingGUI_Update_Postfix(FishingGUI __instance)
    {
        try
        {
            // Drives the sonar's fades and its delayed second blip. Runs in every fishing state, not
            // just the reel game: the frames AFTER the pull are exactly the ones that let the tone
            // fade out instead of being cut off mid-cycle with a click.
            FishingSonar.Tick();

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.F))
            {
                // With NoTimeForFishing installed the catch isn't ours to toggle — it drives it.
                if (DeferToNoTimeForFishing)
                {
                    ScreenReader.Say(Loc.Get("fishing.deferred"));
                    return;
                }
                // Not in the middle of a pull: switching to auto there would silence the sonar
                // mid-reel and hand the player a mini-game they can no longer hear, and switching to
                // manual would drop them into one already half over. The pull lasts a few seconds —
                // the toggle can wait for the next cast.
                if (__instance != null && __instance.state == FishingGUI.FishingState.Pulling) return;
                // auto → manual assisted → manual full speed → auto.
                if (_enabled) { _enabled = false; _assisted = true; }
                else if (_assisted) { _assisted = false; }
                else { _enabled = true; _assisted = true; }

                // NOTE: entering manual does NOT re-arm the briefing. It used to, on the theory
                // that asking for manual is asking to be told how it works — but the cycle passes
                // through manual-assisted every third press, so cycling to hear the modes replayed
                // the whole minute every time round, which is what the player actually experienced.
                // Once per session is enough; Ctrl+H below is there for anyone who wants it again.

                ScreenReader.Say(Loc.Get(_enabled ? "fishing.mode.auto"
                    : _assisted ? "fishing.mode.assisted"
                    : "fishing.mode.full"));

                // …and the briefing lands HERE, queued behind that mode line, rather than waiting
                // for the next ChangeState(BaitChoosing). That transition does not come round again
                // until the window is reopened: pressing Ctrl+F means you are ALREADY in bait
                // choosing, so a player who switches to manual and casts straight away — which is
                // exactly what happens — got the bite ding with no idea what it meant. The
                // 2026-09-20 log shows the whole sequence: mode switched, sonar initialised, hook
                // window missed, briefing never spoken. Bait choosing is also the one state with no
                // clock running, so it is the only place a minute of speech is safe; switch mode
                // anywhere else and we still defer to the next arrival here.
                if (ManualPlay && !_manualIntroSpoken
                    && __instance != null && __instance.state == FishingGUI.FishingState.BaitChoosing)
                {
                    ScreenReader.Say(Loc.Get("fishing.intro.manual.briefing"), interrupt: false);
                    _manualIntroSpoken = true;
                }
                return;
            }

            // Ctrl+H — say the instructions for the mode you are in, in full. The window's own
            // intro shrinks to a one-line reminder after the first time (a minute of speech before
            // every cast is unusable), so there has to be a way back to the long version, and a
            // player who has just lost three fish in a row is exactly who needs it. Blocked during
            // the pull for the same reason as the toggle: it would talk over the sonar, which is
            // the only thing telling them where the fish is.
            if (ctrl && Input.GetKeyDown(KeyCode.H))
            {
                if (__instance != null && __instance.state == FishingGUI.FishingState.Pulling) return;
                ScreenReader.Say(IntroFor(DeferToNoTimeForFishing ? "fishing.intro.deferred"
                    : _enabled ? "fishing.intro.auto"
                    : "fishing.intro.manual.briefing"));
                _manualIntroSpoken = true;
                return;
            }

            // Bait switching — vanilla only binds this to the gamepad shoulder buttons
            // (GameKey.NextTab/PrevTab have NO keyboard binding at all), so a keyboard player can
            // never change bait. We drive the game's own public OnNextBait off Tab, matching the
            // "Tab changes bait" narration. Tab is otherwise dead here: it maps to GameKey.GameGUI,
            // which is gated behind PlayerControlIsDisabled (true during fishing) so it never opens
            // the game menu. RedrawSelectedBait's own postfix speaks the new bait, so we don't
            // announce it here.
            //
            // Forward-only, no Shift+Tab reverse: Shift+Tab was observed to hang the game (Shift is
            // the Dash movement key; something at the engine/input level misbehaves with it held even
            // though OnPrevBait itself is symmetric and safe). Tab cycles through every bait AND the
            // "no bait" state with wraparound, so forward stepping still reaches any bait — the
            // reverse was only a convenience and isn't worth the hang.
            if (!ctrl && __instance != null && __instance.state == FishingGUI.FishingState.BaitChoosing
                && Input.GetKeyDown(KeyCode.Tab))
            {
                __instance.OnNextBait();
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[FISHING] Update postfix error: {ex.Message}");
        }
    }

    // ── State narration (one funnel for every transition) ───────────────────────────────────────

    internal static void FishingGUI_ChangeState_Postfix(FishingGUI __instance, FishingGUI.FishingState target_state)
    {
        try
        {
            var prev = _lastState;
            _lastState = target_state;

            switch (target_state)
            {
                case FishingGUI.FishingState.BaitChoosing:
                    // Bounced back here from the cast bar = the chosen distance had no fish.
                    if (prev == FishingGUI.FishingState.DistanceChoosing)
                        ScreenReader.Say(Loc.Get("fishing.no_fish_there"));
                    else if (DeferToNoTimeForFishing)
                        ScreenReader.Say(IntroFor("fishing.intro.deferred"));
                    else if (_enabled)
                        ScreenReader.Say(IntroFor("fishing.intro.auto"));
                    else
                    {
                        // Manual mode: the long version once, then a reminder that also names the
                        // difficulty, since that is the one thing about it that can change.
                        ScreenReader.Say(IntroFor(!_manualIntroSpoken ? "fishing.intro.manual.briefing"
                            : _assisted ? "fishing.intro.manual.assisted"
                            : "fishing.intro.manual.full"));
                        _manualIntroSpoken = true;
                    }
                    // …and the bait QUEUED behind it, never interrupting it. Show() sets the bait
                    // label and only then changes state, so the bait was already spoken a moment
                    // ago and the line above would cut it off mid-word; RedrawSelectedBait's own
                    // postfix therefore stays quiet outside BaitChoosing and we say it here, in the
                    // order the player needs it: what to do first, then what is on the hook.
                    SayCurrentBait(__instance, interrupt: false);
                    break;

                case FishingGUI.FishingState.DistanceChoosing:
                    // Nothing is spoken here, and that is the fix for a bug that made the cast
                    // unplayable: this used to say "Casting, release E to set distance", and the
                    // very next frame the live tier narration below cut it off after one word. The
                    // bar sweeps its whole range in two seconds, so there is no room for a sentence
                    // here — the instruction belongs in the bait-choosing intro, where the player
                    // has all the time in the world, and it now lives there. The first tier call
                    // ("Near…") is itself the confirmation that the throw has started.
                    _lastAnnouncedTier = -1;   // start a fresh sweep; live narration takes over
                    break;

                case FishingGUI.FishingState.WaitingForBite:
                    ScreenReader.Say(Loc.Get("fishing.waiting"));
                    break;

                case FishingGUI.FishingState.WaitingForPulling:
                    // The fish is on the line. In auto-catch mode the postfix below turns this into a
                    // catch a frame later; in manual mode the player now has the vanilla window to
                    // hook it — and that window is SHORT (the fish presets set it to 0.8-1.0 s). Far
                    // too short to wait for a screen reader to start talking, so the cue is a sound:
                    // a rising two-note ding fires on this very frame, and the spoken "Bite!" follows
                    // whenever the speech queue gets to it.
                    if (ManualPlay)
                    {
                        FishingSonar.Blip(1000f);
                        FishingSonar.BlipAfter(0.12f, 1320f);
                    }
                    ScreenReader.Say(Loc.Get("fishing.bite"));
                    break;

                case FishingGUI.FishingState.Pulling:
                    // Nothing is spoken here on purpose. Pulling is passed through automatically (by
                    // us or by NoTimeForFishing) unless the player is working the reel by hand, and
                    // in that case the sonar starts on this same frame — a sentence of instructions
                    // would mask the one sound the player needs for the next few seconds. What the
                    // tones mean was explained back at bait choosing, where there was time for it.
                    ResetReel();
                    break;

                case FishingGUI.FishingState.TakingOut:
                    // Drop the tone (and any warning blip still queued) the moment the pull is over,
                    // so the result is announced into silence.
                    FishingSonar.Release();
                    AnnounceTakeOut(__instance, prev);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[FISHING] ChangeState postfix error: {ex.Message}");
        }
    }

    // Every arrival at bait choosing needs the same controls explained and only the tail differs by
    // who is doing the catching, so the two are separate strings: the controls are written once
    // instead of five times, and the manual briefing can be spoken ALONE from the Ctrl+F handler,
    // where the player has just been told the mode and wants only the part about the sounds.
    private static string IntroFor(string tailKey) =>
        Loc.Get("fishing.intro.prefix") + " " + Loc.Get(tailKey);

    // Spoken when the line comes out: the caught fish on success, or why it came up empty.
    private static void AnnounceTakeOut(FishingGUI gui, FishingGUI.FishingState prev)
    {
        bool success = false;
        try { success = Traverse.Create(gui).Field("is_success_fishing").GetValue<bool>(); } catch { }

        if (!success)
        {
            // From the waiting stage with nothing hooked = a deliberate/timed-out empty pull;
            // from the reel game = the fish escaped.
            ScreenReader.Say(Loc.Get(prev == FishingGUI.FishingState.Pulling ? "fishing.got_away" : "fishing.empty"), interrupt: false);
            return;
        }

        string name = Loc.Get("fishing.a_fish");
        try
        {
            var fish = Traverse.Create(gui).Field("_fish").GetValue<Item>();
            var raw = fish?.definition?.GetItemName();
            if (!string.IsNullOrEmpty(raw))
            {
                var clean = ScreenReader.StripNguiCodes(raw).Trim();
                if (!string.IsNullOrEmpty(clean)) name = clean;
            }
        }
        catch { }

        ScreenReader.Say(Loc.Fmt("fishing.caught", name), interrupt: false);
    }

    // ── Bait narration ──────────────────────────────────────────────────────────────────────────

    // RedrawSelectedBait already sets the localized bait_name label (or "no bait"); we just voice it
    // whenever the PLAYER changes it — each Tab through the available baits.
    //
    // Show() also calls it, one line before ChangeState(BaitChoosing), and speaking there is worse
    // than useless: the intro that follows immediately interrupts it, so the player hears half a
    // bait name and then the instructions. The state is still None during that call (Hide resets
    // it), which is exactly the signal to stay quiet and let the BaitChoosing case above queue the
    // bait after the intro instead.
    internal static void FishingGUI_RedrawSelectedBait_Postfix(FishingGUI __instance)
    {
        try
        {
            if (__instance == null || __instance.state != FishingGUI.FishingState.BaitChoosing) return;
            SayCurrentBait(__instance, interrupt: true);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[FISHING] RedrawSelectedBait postfix error: {ex.Message}");
        }
    }

    // Speaks the bait label the game has already localized into bait_name ("no bait" included).
    private static void SayCurrentBait(FishingGUI gui, bool interrupt)
    {
        var label = gui?.bait_name?.text;
        if (string.IsNullOrEmpty(label)) return;
        var clean = ScreenReader.StripNguiCodes(label).Trim();
        if (!string.IsNullOrEmpty(clean))
            ScreenReader.Say(Loc.Fmt("fishing.bait", clean), interrupt);
    }

    // ── Live cast-distance narration ────────────────────────────────────────────────────────────

    // While the player holds E, the throw bar sweeps 0→1→0 and UpdateDistanceChoosing maps its
    // value to a distance tier (near/medium/far). We mirror that mapping every frame and speak the
    // current tier — plus whether any fish actually live there — so the player can release on a good
    // one instead of guessing at the timing. Reproduces the tier math from FishingGUI exactly.
    internal static void FishingGUI_UpdateDistanceChoosing_Postfix(FishingGUI __instance)
    {
        try
        {
            // UpdateDistanceChoosing may have already released and changed state; only narrate while
            // the bar is genuinely still sweeping.
            if (__instance.state != FishingGUI.FishingState.DistanceChoosing) return;

            float dist = Traverse.Create(__instance).Field("_throwing_distance").GetValue<float>();
            int tier = Mathf.CeilToInt(dist * 3f) - 1;
            if (tier < 0) tier = 0;
            if (tier > 2) tier = 2;

            if (tier == _lastAnnouncedTier) return;
            _lastAnnouncedTier = tier;

            bool hasFish = false;
            try
            {
                var reservoir = __instance.reservoir_data;
                var lists = __instance.fishes_with_weights;
                hasFish = reservoir != null
                    && reservoir.dist_avaliables != null && tier < reservoir.dist_avaliables.Length
                    && reservoir.dist_avaliables[tier]
                    && lists != null && tier < lists.Length && lists[tier] != null && lists[tier].Count > 0;
            }
            catch { }

            string tierName = Loc.Get(tier == 0 ? "fishing.tier.near" : tier == 1 ? "fishing.tier.medium" : "fishing.tier.far");
            ScreenReader.Say(Loc.Fmt(hasFish ? "fishing.tier.has_fish" : "fishing.tier.empty", tierName));
        }
        catch (Exception ex)
        {
            _log?.LogError($"[FISHING] UpdateDistanceChoosing postfix error: {ex.Message}");
        }
    }

    // ── Auto-catch (the two twitch steps) ───────────────────────────────────────────────────────

    // Auto-catch — the fish is hooked (WaitingForPulling) and already selected (FishingGUI set
    // _fish/_fish_preset just before this state). We mark the pull a success and drive straight to
    // TakingOut, awarding exactly the fish that bait+distance chose.
    //
    // The catch: TakingOut's award (UpdateTakingOut) is gated on taking_out_animation_finished,
    // which is ONLY set by EndOfAnimEvent — a StateMachineBehaviour.OnStateExit on the take-out
    // ANIMATOR state. When we move the state machine programmatically the animator never actually
    // enters (and so never exits) that clip, the flag stays false, and UpdateTakingOut loops forever
    // narrating "Caught X" but never handing over the fish (the hang the player hit — twice). So we
    // set the public flag ourselves: no dependence on animator timing, the award runs next frame.
    // A blind player doesn't see the reel animation anyway; Hide() resets the character pose to Idle.
    // Runs only when WE own the catch (auto-catch on, NoTimeForFishing absent).
    internal static void FishingGUI_UpdateWaitingForPulling_Postfix(FishingGUI __instance)
    {
        try
        {
            if (DeferToNoTimeForFishing || !_enabled) return;
            if (__instance.state != FishingGUI.FishingState.WaitingForPulling) return;

            var t = Traverse.Create(__instance);
            t.Field("is_success_fishing").SetValue(true);
            // ChangeState(TakingOut) resets taking_out_animation_finished to false, so set it true
            // AFTER — that's the flag UpdateTakingOut waits on before awarding and closing.
            t.Method("ChangeState", new object[] { FishingGUI.FishingState.TakingOut }).GetValue();
            __instance.taking_out_animation_finished = true;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[FISHING] auto-catch error: {ex.Message}");
        }
    }

    // ── Manual play: the reel game by ear ───────────────────────────────────────────────────────

    // True when the player is actually meant to play the mini-game themselves: auto-catch off and no
    // NoTimeForFishing doing it for them. Everything in this section is gated on it.
    private static bool ManualPlay => !_enabled && !DeferToNoTimeForFishing;

    // How far (in bar units, the game's 0-100 scale) the fish has to be outside the bar before the
    // tone reaches its full octave of detune. The fish rarely strays further than this, so the pitch
    // range gets used across the whole realistic span instead of saturating immediately.
    private const float FullDetuneDistance = 45f;

    // The pitch offset at the bar's own edge — the seam where the inside and outside halves of the
    // mapping meet. Small enough that the bar still reads as "the middle", big enough to hear a fish
    // sliding towards the edge in time to do something about it.
    private const float EdgeSemitones = 2f;

    // How much the assisted difficulty slows the fish down. It scales the time step fed to the
    // fish's curve, so the fish swims exactly the same path, just at half pace — nothing about the
    // catch is changed, only how fast you have to be. See FishPreset_CalculateFishPos_Prefix.
    private const float AssistedFishSpeed = 0.5f;

    // Milestone blips as the catch progress bar fills and empties. Rising triad up, duller tones
    // down, so gaining and losing ground never sound alike.
    private static readonly float[] ProgressUpHz = { 700f, 900f, 1150f };
    private static readonly float[] ProgressDownHz = { 600f, 500f, 420f };

    private static int _progressLevel;       // 0-3: which quarter of the bar we last reported
    private static float _zeroProgressTime;  // how long the bar has been empty (the fail clock)
    private static float _lastWarningAt;
    private static bool _rodAtBottom;
    private static bool _rodAtTop;

    // Called when the reel game starts. _rodAtBottom starts TRUE because the rod genuinely does
    // start resting at the bottom — without this the first frame would report a bump that never
    // happened, on top of the bite ding.
    private static void ResetReel()
    {
        _progressLevel = 0;
        _zeroProgressTime = 0f;
        _lastWarningAt = 0f;
        _rodAtBottom = true;
        _rodAtTop = false;
    }

    /// <summary>
    /// The reel game, once per frame: read where the fish and the bar are and turn that into sound.
    ///
    /// The mini-game is a tracking task. A bar of 30-38 units (by rod level) sits somewhere on a
    /// 0-100 column and is flown like a flappy-bird: holding the interaction key thrusts it up,
    /// releasing lets gravity pull it down. A fish wanders the same column on a sum of sines. While
    /// the fish is inside the bar the catch progress fills (about 0.45/s); while it is outside the
    /// progress drains (about 0.5/s), and if it sits empty for the fish's fail time (2-3 s) the fish
    /// escapes. So the player needs two things continuously: which way to move, and whether they are
    /// scoring right now. Pitch answers the first, the steady-versus-pulsing tone answers the second.
    ///
    /// Everything read here is public on FishingGUI: the widget positions are the same numbers
    /// UpdatePulling just wrote, divided back out by the pixels-per-unit factor the game derived from
    /// the progress backdrop's height. No reflection, so nothing to go stale.
    /// </summary>
    internal static void FishingGUI_UpdatePulling_Postfix(FishingGUI __instance)
    {
        try
        {
            if (!ManualPlay || __instance == null) return;
            // UpdatePulling also runs on the frame the fish is landed or lost (it changes state
            // itself); on that frame we simply stop asking for a tone and it fades.
            if (__instance.state != FishingGUI.FishingState.Pulling) return;
            if (__instance.fishing_rod == null || __instance.fish_tf == null || __instance.process_back == null) return;

            float screenK = __instance.process_back.height / 100f;
            if (screenK <= 0.001f) return;   // layout not sized yet — silence beats a screech

            float rodBottom = __instance.fishing_rod.transform.localPosition.y / screenK;
            float rodHeight = __instance.fishing_rod.height / screenK;
            float rodTop = rodBottom + rodHeight;
            float fishPos = __instance.fish_tf.localPosition.y / screenK;
            float progress = (__instance.progress_bar != null) ? __instance.progress_bar.value : 0f;

            // ── The carrier: where is the fish, relative to the bar? ──
            //
            // Pitch is a continuous, monotonic function of the fish's position across the whole
            // column, in two joined halves:
            //   inside the bar  — the base pitch at dead centre, sliding to ±2 semitones at the
            //                     edges. This is what tells you the fish is DRIFTING before it
            //                     leaves: a flat tone across a bar a third of the column wide gives
            //                     no warning at all, so the first news of trouble is the tone
            //                     breaking into a pulse, by which time you are already chasing.
            //   outside the bar — starts at exactly those ±2 semitones, where the inside half left
            //                     off, and widens to ±12 with distance.
            // The two meet at the edge, so nothing jumps; inside versus outside is carried by the
            // tone being steady versus pulsing, not by the pitch.
            bool inZone = fishPos >= rodBottom && fishPos <= rodTop;
            float semitones;
            if (inZone)
            {
                float half = Mathf.Max(rodHeight * 0.5f, 0.001f);
                semitones = ((fishPos - (rodBottom + half)) / half) * EdgeSemitones;
            }
            else
            {
                float offset = (fishPos > rodTop) ? fishPos - rodTop : fishPos - rodBottom;
                semitones = Mathf.Sign(offset) * (EdgeSemitones + 10f * Mathf.Clamp01(Mathf.Abs(offset) / FullDetuneDistance));
            }
            FishingSonar.SetTone(semitones, inZone);

            // ── Milestones: the catch bar crossing a quarter, in either direction ──
            int level = Mathf.Clamp(Mathf.FloorToInt(progress / 0.25f), 0, 3);
            if (level > _progressLevel) FishingSonar.Blip(ProgressUpHz[Mathf.Min(level, 3) - 1], 0.8f);
            else if (level < _progressLevel) FishingSonar.Blip(ProgressDownHz[level], 0.6f);
            _progressLevel = level;

            // ── The fail clock ──
            // An empty bar is not itself an emergency: it is where every pull starts. It becomes one
            // once it stays empty, so the warning waits a second before it begins, and then repeats
            // as a low double tick — nothing else in the mini-game is both that low and repeating.
            if (progress <= 0.0005f)
            {
                _zeroProgressTime += Time.deltaTime;
                if (_zeroProgressTime > 1f && Time.unscaledTime - _lastWarningAt > 0.5f)
                {
                    _lastWarningAt = Time.unscaledTime;
                    FishingSonar.Blip(196f, 0.9f);
                    FishingSonar.BlipAfter(0.11f, 196f, 0.9f);
                }
            }
            else _zeroProgressTime = 0f;

            // ── Bar hitting its limits ──
            // Worth its own cue: pinned at the bottom, "go lower" is advice the player cannot take,
            // and the pitch alone would never say so.
            float rodMax = 100f - rodHeight;
            bool atBottom = rodBottom <= 0.35f;
            bool atTop = rodBottom >= rodMax - 0.35f;
            // 130 Hz, not the 165 the carrier itself reaches when the fish is far below the bar:
            // those two fire together constantly, and at the same pitch they would blur into one.
            if (atBottom && !_rodAtBottom) FishingSonar.Blip(130f, 0.5f);
            if (atTop && !_rodAtTop) FishingSonar.Blip(1320f, 0.35f);
            _rodAtBottom = atBottom;
            _rodAtTop = atTop;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[FISHING] reel sonar error: {ex.Message}");
        }
    }

    /// <summary>
    /// The assisted difficulty, in one line: feed the fish's curve a smaller time step.
    ///
    /// FishPreset.CalculateFishPos advances three phases by delta_time and adds up the sines; every
    /// one of them scales with that step, so halving it halves the fish's speed while leaving its
    /// path, its range and the shape of its wandering untouched. Nothing else in the mini-game reads
    /// this method — FishLogic is its only caller — so the progress rates, the fail clock, the bar,
    /// the rod physics and the fish that bit are all exactly as the game shipped them.
    ///
    /// Done here rather than by editing the preset: FishPreset is a ScriptableObject that Resources
    /// caches for the rest of the session, so a mod that writes to its fields has to remember to put
    /// every one of them back — and if it ever fails to, the player's game stays modified until they
    /// restart it. A scaled argument cannot leak.
    /// </summary>
    internal static void FishPreset_CalculateFishPos_Prefix(ref float delta_time)
    {
        try
        {
            if (ManualPlay && _assisted) delta_time *= AssistedFishSpeed;
        }
        catch { }
    }

    // Closing the fishing UI (caught, lost, or escaped out of) ends the Update calls that drive the
    // sonar's fade, so this is the one place that has to cut it off outright.
    internal static void FishingGUI_Hide_Postfix()
    {
        try
        {
            FishingSonar.StopNow();
            ResetReel();
        }
        catch (Exception ex)
        {
            _log?.LogError($"[FISHING] Hide postfix error: {ex.Message}");
        }
    }
}
