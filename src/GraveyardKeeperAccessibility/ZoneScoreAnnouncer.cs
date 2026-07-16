namespace GraveyardKeeperAccessibility;

/// <summary>
/// Announces zone ratings when the player presses G. Always reports the two persistent goal
/// zones — graveyard and church — and, first, whatever scored zone the player is currently
/// standing in (the alchemy cellar, the tavern…). That "current zone" branch is what makes the
/// cellar-decoration quest ("reach 20 points") checkable: the game shows the same number on the
/// HUD banner while you're in the zone (see <see cref="PlayerComponent.UpdateZone"/>, which fills
/// the banner description with <c>zone.GetQualityString()</c> for any zone whose definition has a
/// quality). We read the live total from <see cref="WorldZone.GetTotalQuality"/> and name the zone
/// with the same localized string the banner uses (<c>GJL.L("zone_" + id)</c>).
/// </summary>
internal static class ZoneScoreAnnouncer
{
    private static ManualLogSource _log;

    // The persistent goal zones reported wherever the player is. Any other scored zone (cellar,
    // tavern) is picked up automatically via the player's current zone, so it needs no id here.
    private static readonly string[] GoalZones = { "graveyard", "church" };

    internal static void Init(ManualLogSource log)
    {
        _log = log;
    }

    internal static void Announce()
    {
        try
        {
            if (!MainGame.game_started || MainGame.me == null)
            {
                ScreenReader.Say("No game in progress");
                return;
            }

            var parts = new List<string>();
            var reported = new HashSet<string>();

            // First: the scored zone the player is currently inside. This is the one being decorated
            // right now (e.g. the cellar), so it's the most relevant and picks up any zone the game
            // rates without us hard-coding its id.
            AddZoneReport(CurrentPlayerZone(), parts, reported, requireScored: true);

            // Then the two big persistent goals, reachable from anywhere.
            foreach (var id in GoalZones)
            {
                var zone = WorldZone.GetZoneByID(id, null_is_error: false);
                if (zone != null)
                    AddZoneReport(zone, parts, reported, requireScored: false, fallbackId: id);
                else
                    AddFallbackParam(id, parts, reported);
            }

            ScreenReader.Say(parts.Count > 0 ? string.Join(". ", parts) : "No zone scores available");
        }
        catch (Exception ex)
        {
            _log?.LogError($"ZoneScoreAnnouncer error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>The scored WorldZone the player currently overlaps, or null when in open ground.</summary>
    private static WorldZone CurrentPlayerZone()
    {
        try { return MainGame.me?.player?.GetMyWorldZone(); }
        catch { return null; }
    }

    /// <summary>
    /// Append "&lt;Name&gt; rating: N" for a zone, unless it's already reported. When
    /// <paramref name="requireScored"/> is set, an unscored zone (calc_method None — e.g. Town) is
    /// skipped so pressing G in an ordinary area doesn't announce a meaningless zero.
    /// </summary>
    private static void AddZoneReport(WorldZone zone, List<string> parts, HashSet<string> reported,
        bool requireScored, string fallbackId = null)
    {
        if (zone == null) return;
        var id = zone.id;
        if (string.IsNullOrEmpty(id) || reported.Contains(id)) return;

        if (requireScored && !IsScored(zone)) return;

        float rating = zone.GetTotalQuality();
        parts.Add($"{ZoneName(id, fallbackId)} rating: {Format(rating)}");
        reported.Add(id);

        if (id == "graveyard")
        {
            var skulls = SummarizeZoneSkulls(zone);
            if (!string.IsNullOrEmpty(skulls))
                parts.Add(skulls);
        }
    }

    // A goal zone that isn't currently loaded: fall back to the last end-of-day value the game
    // stored on the player (e.g. "graveyard_qual"), so G still reports something meaningful.
    private static void AddFallbackParam(string id, List<string> parts, HashSet<string> reported)
    {
        if (reported.Contains(id)) return;
        try
        {
            var player = MainGame.me?.player;
            if (player == null) return;
            float rating = player.GetParam(id + "_qual");
            parts.Add($"{ZoneName(id, id)} rating: {Format(rating)}");
            reported.Add(id);
        }
        catch { }
    }

    private static bool IsScored(WorldZone zone)
    {
        try
        {
            return zone.definition != null
                && zone.definition.calc_method != WorldZoneDefinition.QualityCalcMethod.None;
        }
        catch { return false; }
    }

    /// <summary>
    /// Localized zone name, the same one the HUD banner shows (<c>GJL.L("zone_" + id)</c>). Falls
    /// back to a capitalized id when there's no translation, so a spoken label is always sensible.
    /// </summary>
    private static string ZoneName(string id, string fallbackId)
    {
        try
        {
            var key = "zone_" + id;
            var loc = ScreenReader.StripNguiCodes(GJL.L(key) ?? "").Trim();
            if (!string.IsNullOrEmpty(loc) && loc != key)
                return loc;
        }
        catch { }

        var basis = string.IsNullOrEmpty(id) ? fallbackId : id;
        if (string.IsNullOrEmpty(basis)) return "Zone";
        return char.ToUpperInvariant(basis[0]) + basis.Substring(1);
    }

    private static string Format(float value)
    {
        return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Sum red/white skulls over every body interred in the zone's graves.
    private static string SummarizeZoneSkulls(WorldZone zone)
    {
        try
        {
            int red = 0, white = 0, graves = 0;
            foreach (var wgo in zone.GetZoneWGOs())
            {
                if (wgo == null) continue;
                var body = wgo.GetBodyFromInventory();
                if (body == null || body.definition == null
                    || body.definition.type != ItemDefinition.ItemType.Body || body.IsEmpty())
                    continue;

                body.GetBodySkulls(out int r, out int w, out _);
                red += r;
                white += w;
                graves++;
            }

            if (graves == 0) return null;
            return $"{red} red, {white} white across {graves} graves";
        }
        catch (Exception ex)
        {
            _log?.LogError($"ZoneScoreAnnouncer skull summary error: {ex.Message}");
            return null;
        }
    }
}
