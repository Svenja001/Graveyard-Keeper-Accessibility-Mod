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
/// This module (patches live in this class, dispatched from Plugin.RegisterPatches) does three
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
///   3. TOGGLE — Ctrl+F flips auto-catch off, leaving the vanilla mini-game for a sighted-assisted
///      or practising player. Off by default would make fishing unplayable blind, so it defaults ON.
///
/// All private FishingGUI state is reached with Harmony's Traverse; everything is wrapped in
/// try/catch so a reflection miss can never wedge the fishing UI.
/// </summary>
internal static class FishingAssist
{
    private static ManualLogSource _log;
    private static bool _enabled = true;   // auto-catch on by default (see class summary)

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

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _log?.LogInfo("[FISHING] FishingAssist initialized (auto-catch on, Ctrl+F toggles)");
    }

    // ── Toggle (polled every frame the fishing UI is open) ──────────────────────────────────────

    internal static void FishingGUI_Update_Postfix()
    {
        try
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.F))
            {
                // With NoTimeForFishing installed the catch isn't ours to toggle — it drives it.
                if (DeferToNoTimeForFishing)
                {
                    ScreenReader.Say("Catch is handled by the No Time For Fishing mod.");
                    return;
                }
                _enabled = !_enabled;
                ScreenReader.Say(_enabled ? "Auto catch on" : "Auto catch off, manual fishing");
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
                        ScreenReader.Say("No fish at that distance. Choose bait and cast again.");
                    else if (DeferToNoTimeForFishing)
                        ScreenReader.Say("Fishing. Tab changes bait, hold E to cast. Catch is automatic.");
                    else
                        ScreenReader.Say(_enabled
                            ? "Fishing. Tab changes bait, hold E to cast. Auto catch is on, Control F to toggle."
                            : "Fishing. Tab changes bait, hold E to cast. Manual fishing.");
                    break;

                case FishingGUI.FishingState.DistanceChoosing:
                    _lastAnnouncedTier = -1;   // start a fresh sweep; live narration takes over
                    ScreenReader.Say("Casting. Release E to set distance.");
                    break;

                case FishingGUI.FishingState.WaitingForBite:
                    ScreenReader.Say("Line cast. Waiting for a bite.");
                    break;

                case FishingGUI.FishingState.WaitingForPulling:
                    // The fish is on the line. In auto-catch mode the prefix below turns this into a
                    // catch a frame later; in manual mode the player now has the vanilla window.
                    ScreenReader.Say("Bite!");
                    break;

                case FishingGUI.FishingState.Pulling:
                    // Pulling is passed through automatically (by us or by NoTimeForFishing) unless
                    // the player is truly working the reel by hand — auto-catch off AND NTFF absent.
                    // Only then do we spell out the controls; otherwise stay quiet so the flow reads
                    // cleanly as "Bite!" then "Caught X" without a misleading instruction in between.
                    if (!DeferToNoTimeForFishing && !_enabled)
                        ScreenReader.Say("Reeling in. Hold E to raise, release to lower, keep the fish in the bar.");
                    break;

                case FishingGUI.FishingState.TakingOut:
                    AnnounceTakeOut(__instance, prev);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[FISHING] ChangeState postfix error: {ex.Message}");
        }
    }

    // Spoken when the line comes out: the caught fish on success, or why it came up empty.
    private static void AnnounceTakeOut(FishingGUI gui, FishingGUI.FishingState prev)
    {
        bool success = false;
        try { success = Traverse.Create(gui).Field("is_success_fishing").GetValue<bool>(); } catch { }

        if (!success)
        {
            // From the waiting stage with nothing hooked = a deliberate/timed-out empty pull;
            // from the reel game = the fish escaped.
            ScreenReader.Say(prev == FishingGUI.FishingState.Pulling ? "The fish got away." : "Reeled in empty.", interrupt: false);
            return;
        }

        string name = "a fish";
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

        ScreenReader.Say($"Caught {name}!", interrupt: false);
    }

    // ── Bait narration ──────────────────────────────────────────────────────────────────────────

    // RedrawSelectedBait already sets the localized bait_name label (or "no bait"); we just voice it
    // whenever it changes — on open and on each Tab through the available baits.
    internal static void FishingGUI_RedrawSelectedBait_Postfix(FishingGUI __instance)
    {
        try
        {
            var label = __instance?.bait_name?.text;
            if (string.IsNullOrEmpty(label)) return;
            var clean = ScreenReader.StripNguiCodes(label).Trim();
            if (!string.IsNullOrEmpty(clean))
                ScreenReader.Say($"Bait: {clean}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[FISHING] RedrawSelectedBait postfix error: {ex.Message}");
        }
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

            string tierName = tier == 0 ? "Near" : tier == 1 ? "Medium" : "Far";
            ScreenReader.Say(hasFish ? $"{tierName}, fish here" : $"{tierName}, empty");
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
}
