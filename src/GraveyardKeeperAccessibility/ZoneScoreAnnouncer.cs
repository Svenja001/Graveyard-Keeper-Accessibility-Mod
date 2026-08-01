namespace GraveyardKeeperAccessibility;

/// <summary>
/// Announces zone ratings when the player presses G. Always reports the two persistent goal
/// zones — graveyard and church — and, first, every scored zone the player is currently standing
/// in (the alchemy cellar, the player's tavern, the refugee camp…). That "current zone" branch is
/// what makes the cellar-decoration quest ("reach 20 points") checkable: the game shows the same
/// number on the HUD banner while you're in the zone (see <see cref="PlayerComponent.UpdateZone"/>,
/// which fills the banner description with <c>zone.GetQualityString()</c> for any zone whose
/// definition has a quality).
///
/// We deliberately do NOT use <c>GetMyWorldZone()</c> here, even though the HUD does: it returns
/// the FIRST overlapping zone collider Physics2D happens to hand back, and the DLC zones sit on top
/// of base-game ones (the Stranger Sins "players_tavern" shares the town tavern building, the Game
/// of Crone "refugees_camp" sits inside the old camp area). Whenever the base zone won that race the
/// scored DLC zone was silently dropped, so G never spoke the tavern reputation or the refugee
/// satisfaction. Collecting ALL overlapping zones fixes that and costs one extra loop.
///
/// Most zones read out as "&lt;Name&gt; rating: N". Zones whose balance format pulls in a second
/// value — the camp's <c>@{$cur_refugee_happiness:0.##} / {0:0}</c>, the Souls DLC's gratitude
/// points — are spoken with the game's own localized string instead, because there the plain zone
/// total is only the capacity, not the number the player is working on.
/// </summary>
internal static class ZoneScoreAnnouncer
{
    private static ManualLogSource _log;

    // The persistent goal zones reported wherever the player is. Any other scored zone (cellar,
    // tavern, refugee camp) is picked up automatically via the player's current zones.
    private static readonly string[] GoalZones = { "graveyard", "church" };

    // Physics layer the zone colliders live on — the same mask WorldGameObject.GetMyWorldZone uses.
    private const int ZoneColliderMask = 1 << 19;

    // Stranger Sins: reputation stars are stored as items inside the tavern's star-bar object,
    // exactly like the camp keeps its satisfaction in "refugee_happiness_item"s.
    private const string TavernBarObjId = "tavern_reputation_bar";
    private const string TavernStarItemId = "tavern_reputation_item";

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

            // First: the scored zones the player is currently inside. These are the ones being
            // worked on right now (cellar, tavern, camp), so they're the most relevant, and they
            // need no hard-coded ids.
            foreach (var zone in CurrentPlayerZones())
                AddZoneReport(zone, parts, reported, requireScored: true);

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

    /// <summary>
    /// Every WorldZone whose collider overlaps the player, not just the first one. Zones nest and
    /// overlap (a DLC zone laid over a base-game one), and only the full list can tell us that the
    /// player is standing in, say, both "tavern" and the scored "players_tavern".
    /// </summary>
    private static List<WorldZone> CurrentPlayerZones()
    {
        var zones = new List<WorldZone>();
        try
        {
            var player = MainGame.me?.player;
            if (player == null || player.is_removed) return zones;

            var hits = Physics2D.OverlapPointAll(player.transform.position, ZoneColliderMask);
            if (hits == null) return zones;

            foreach (var hit in hits)
            {
                if (hit == null) continue;
                var zone = hit.gameObject.GetComponentInParent<WorldZone>();
                if (zone == null || zones.Contains(zone)) continue;
                if (zone.IsDisabled()) continue;
                // A DLC zone the player doesn't own still sits active in the scene as set-dressing —
                // never voice its score (same gate ZoneAnnouncer applies to the banner).
                if (!ObjectNavigator.IsZoneDlcAvailable(zone.id)) continue;
                zones.Add(zone);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"ZoneScoreAnnouncer zone scan error: {ex.Message}");
        }
        return zones;
    }

    /// <summary>
    /// Append a zone's readout, unless it's already reported. When <paramref name="requireScored"/>
    /// is set, an unscored zone (calc_method None — e.g. Town) is skipped so pressing G in an
    /// ordinary area doesn't announce a meaningless zero.
    /// </summary>
    private static void AddZoneReport(WorldZone zone, List<string> parts, HashSet<string> reported,
        bool requireScored, string fallbackId = null)
    {
        if (zone == null) return;
        var id = zone.id;
        if (string.IsNullOrEmpty(id) || reported.Contains(id)) return;

        if (requireScored && !IsScored(zone)) return;

        reported.Add(id);
        var name = ZoneName(id, fallbackId);

        // Camp/Souls-style zones carry their real number inside the game's own format string.
        var gameText = ExtraValueQualityText(zone);
        parts.Add(gameText != null
            ? $"{name}: {gameText}"
            : $"{name} rating: {Format(zone.GetTotalQuality())}");

        string extra = null;
        if (id == "graveyard") extra = SummarizeZoneSkulls(zone);
        else if (id == "refugees_camp") extra = CampSatisfactionTrend();
        else if (id == "players_tavern") extra = TavernReputation(zone);

        if (!string.IsNullOrEmpty(extra))
            parts.Add(extra);
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
    /// The game's own localized quality line, but only for zones whose balance format shows more
    /// than the bare zone total. "@{0:0.#}" (graveyard, church, tavern) is just an icon plus the
    /// total, which our "rating: N" wording says better; "@{$cur_refugee_happiness:0.##} / {0:0}"
    /// (refugee camp) and "@{$gratitude_points:0} / {0:0}" (Souls) carry a second, more important
    /// number that only the game can format. Returns null when there's nothing extra to say.
    /// </summary>
    private static string ExtraValueQualityText(WorldZone zone)
    {
        try
        {
            var fmt = zone.definition?.string_format;
            if (string.IsNullOrEmpty(fmt)) return null;
            // "{$param}" pulls in a player param, "%zone_id" the total of another zone.
            if (!fmt.Contains("{$") && !fmt.Contains("%")) return null;
            return CleanZoneText(zone.GetQualityString());
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"ZoneScoreAnnouncer quality string error ({zone?.id}): {ex.Message}");
            return null;
        }
    }

    /// <summary>Make a zone's HUD string speakable: drop sprite tokens, read "/" as "of".</summary>
    private static string CleanZoneText(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = ScreenReader.StripNguiCodes(text);
        // Each zone prefixes its own currency sprite — "(refugee_happiness_slot)", "(tavern)",
        // "(soul_zone_capacity)" — which has no spoken meaning once the zone is named.
        text = Regex.Replace(text, @"\([a-zA-Z0-9_]+\)", " ");
        text = text.Replace("/", " of ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// Which way the refugee camp's satisfaction is heading, mirroring the camp widget
    /// (<see cref="CampQualityWidget.Redraw"/>): how far the bar has filled toward the next
    /// satisfaction point, and the change the game predicts for one more day.
    ///
    /// The important special case is the cap. Satisfaction is hard-clamped to the camp's slot count
    /// in <see cref="RefugeesCampEngine.UpdateHappiness"/> (slots = the camp zone's rounded quality,
    /// 16 at most), so once the bar reads "3 of 3" more food changes nothing — the game keeps
    /// computing a positive delta and throws it away ("Can not add item [refugee_happiness_item:0]"
    /// in Player.log). Speaking the predicted rise there would be a lie, so we say it's capped and
    /// name the way out instead: build more camp facilities to add slots.
    /// </summary>
    private static string CampSatisfactionTrend()
    {
        try
        {
            var engine = DLCRefugees.RefugeesCampEngine.instance;
            if (engine == null) return null;

            float progress = engine.GetCampHappinessProgress();
            int filled = engine.GetTotalHappiness();

            int slots = CampSlots();
            if (slots > 0 && filled >= slots)
                return slots >= DLCRefugees.RefugeesCampEngine.MAX_REFUGEE_CAMP_QUALITY
                    ? "at the maximum, the camp is fully developed"
                    : $"at the limit of {slots}, build more camp facilities to raise it, up to "
                      + $"{DLCRefugees.RefugeesCampEngine.MAX_REFUGEE_CAMP_QUALITY}";

            float perDay = engine.PredictRefugeeHappinessChange(1f);
            // The widget clamps a drop that would push an already-empty bar below zero.
            if (filled <= 0 && progress + perDay < 0f) perDay = -progress;

            int pct = Mathf.RoundToInt(progress * 100f);
            int trend = Mathf.RoundToInt(perDay * 100f);
            string trendText = trend > 0
                ? $"rising {trend} percent per day"
                : trend < 0 ? $"falling {-trend} percent per day" : "steady";

            return $"{pct} percent toward the next point, {trendText}";
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"ZoneScoreAnnouncer camp trend error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// How many satisfaction slots the camp currently has — the exact number the engine clamps
    /// against, read off the camp progress object it keeps the value on. 0 when unavailable.
    /// </summary>
    private static int CampSlots()
    {
        try
        {
            var progressObj = WorldMap.GetWorldGameObjectByCustomTag(
                DLCRefugees.RefugeesCampEngine.CAMP_PROGRESS_OBJ_CUSTOM_TAG, ignore_not_found_error: true);
            if (progressObj == null || progressObj.is_removed) return 0;
            return progressObj.GetParamInt(DLCRefugees.RefugeesCampEngine.CAMP_AVAILABLE_SLOTS_ITEM_NAME);
        }
        catch { return 0; }
    }

    /// <summary>
    /// The player tavern's reputation stars plus what the zone rating currently buys. Stars live as
    /// "tavern_reputation_item"s inside the star-bar object and are what tavern events cost; the
    /// zone rating itself decides the alcohol sales bonus (see
    /// <see cref="PlayersTavernEngine.CalculateAlcoholSellingBonus"/>).
    /// </summary>
    private static string TavernReputation(WorldZone zone)
    {
        var bits = new List<string>();
        try
        {
            var bar = WorldMap.GetWorldGameObjectByObjId(TavernBarObjId, ignore_not_found_error: true);
            if (bar != null && !bar.is_removed)
            {
                int stars = bar.data.GetItemsCount(TavernStarItemId);
                bits.Add(stars == 1 ? "reputation: 1 star" : $"reputation: {stars} stars");
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"ZoneScoreAnnouncer tavern star error: {ex.Message}");
        }

        try
        {
            float quality = zone.GetTotalQuality();
            if (quality > 55f) bits.Add("alcohol sales bonus: 20 percent");
            else if (quality > 30f) bits.Add("alcohol sales bonus: 10 percent, 55 rating for 20 percent");
            else bits.Add("no alcohol sales bonus yet, 30 rating for 10 percent");
        }
        catch { }

        return bits.Count > 0 ? string.Join(", ", bits) : null;
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
