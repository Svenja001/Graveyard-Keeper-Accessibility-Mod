namespace GraveyardKeeperAccessibility;

/// <summary>
/// Announces the map area the player walks into — "Town", "Graveyard", "Church" — so a blind
/// player always knows when they cross from one named zone to another.
///
/// The game already does the hard part for sighted players: every 0.5s
/// <see cref="PlayerComponent.UpdateZone"/> resolves the current <see cref="WorldZone"/> and
/// pushes its localized name to the HUD banner via <c>HUD.UpdateZoneInfo(name, descr)</c>
/// (name = <c>GJL.L("zone_" + zone.id)</c>). We hook that one method and voice the name, so the
/// spoken area matches the on-screen banner exactly and is already in the player's language.
///
/// The banner refreshes every half-second with the same name while you stand in one area, so we
/// only speak when the name CHANGES. When you leave a named area into open ground the game passes
/// its placeholder "..." — we say "Wilderness" instead so the transition is still audible.
/// </summary>
internal static class ZoneAnnouncer
{
    private static ManualLogSource _log;

    // Spoken whenever the player leaves a named zone for open ground (the game's "..." banner).
    private const string WildernessLabel = "Wilderness";

    // Last text we announced, so the every-0.5s banner refresh doesn't repeat itself.
    private static string _lastSpoken;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _lastSpoken = null;
        _log?.LogInfo("[ZONE] ZoneAnnouncer initialized");
    }

    /// <summary>Clear the de-dup cache so a save reload re-announces the current area once.</summary>
    internal static void Reset()
    {
        _lastSpoken = null;
    }

    /// <summary>
    /// Postfix target for <c>HUD.UpdateZoneInfo</c>. Speaks the area name on change.
    /// </summary>
    internal static void OnZoneBanner(string name)
    {
        try
        {
            if (!MainGame.game_started) return;

            // "..." is the game's no-zone placeholder (player is in open wilderness).
            string text = name == "..."
                ? WildernessLabel
                : ScreenReader.StripNguiCodes(name ?? "").Trim();

            if (string.IsNullOrEmpty(text)) return;
            if (text == _lastSpoken) return;   // same area as last banner — stay quiet

            _lastSpoken = text;
            _log?.LogInfo($"[ZONE] Entered {text}");
            // Non-interrupting so an area change mid-walk doesn't cut off a pickup/quest line.
            ScreenReader.Say(text, interrupt: false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[ZONE] OnZoneBanner error: {ex.Message}");
        }
    }
}
