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
///      tracking. This is the same idea stardew-access uses for Stardew fishing.
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
                    // Only reached in manual mode (auto-catch skips straight to take-out).
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

    // The bite has landed and the fish is selected (FishingGUI set _fish/_fish_preset just before
    // this state). With auto-catch on we skip both the reaction window and the reel tracking game:
    // mark the pull a success and drive the state machine to TakingOut, which plays the take-out
    // animation and awards _fish exactly as a perfect manual catch would. Returning false skips the
    // original UpdateWaitingForPulling so it can't bounce us back to WaitingForBite first.
    internal static bool FishingGUI_UpdateWaitingForPulling_Prefix(FishingGUI __instance)
    {
        try
        {
            if (!_enabled) return true;   // manual mode: run the vanilla reaction window

            var t = Traverse.Create(__instance);
            t.Field("is_success_fishing").SetValue(true);
            t.Method("ChangeState", new object[] { FishingGUI.FishingState.TakingOut }).GetValue();
            return false;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[FISHING] auto-catch prefix error: {ex.Message}");
            return true;   // on any failure, fall back to the real mini-game rather than hang
        }
    }
}
