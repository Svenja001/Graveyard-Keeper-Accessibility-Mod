namespace GraveyardKeeperAccessibility;

/// <summary>
/// Announces the current zone ratings (graveyard, church) when the player presses G.
/// These are the same totals the game shows sighted players via the quality display;
/// we read them live from <see cref="WorldZone.GetTotalQuality"/>.
/// </summary>
internal static class ZoneScoreAnnouncer
{
    private static ManualLogSource _log;

    // Zone id -> spoken label. These match the zones the game itself tracks
    // (see EnvironmentEngine.OnEndOfDay, which scores "graveyard" and "church").
    private static readonly (string id, string label)[] Zones =
    {
        ("graveyard", "Graveyard"),
        ("church", "Church"),
    };

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
            foreach (var (id, label) in Zones)
            {
                var zone = WorldZone.GetZoneByID(id, null_is_error: false);
                float rating;
                if (zone != null)
                    rating = zone.GetTotalQuality();
                else if (MainGame.me.player != null)
                    rating = MainGame.me.player.GetParam(id + "_qual"); // last end-of-day value
                else
                    continue;

                parts.Add($"{label} rating: {Format(rating)}");

                // For the graveyard, also total the red/white skulls across all interred bodies.
                if (id == "graveyard" && zone != null)
                {
                    var skulls = SummarizeZoneSkulls(zone);
                    if (!string.IsNullOrEmpty(skulls))
                        parts.Add(skulls);
                }
            }

            ScreenReader.Say(parts.Count > 0 ? string.Join(". ", parts) : "No zone scores available");
        }
        catch (Exception ex)
        {
            _log?.LogError($"ZoneScoreAnnouncer error: {ex.Message}\n{ex.StackTrace}");
        }
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
