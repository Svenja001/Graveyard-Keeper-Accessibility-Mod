namespace GraveyardKeeperAccessibility;

/// <summary>
/// Readable names for world objects the GAME never translates.
///
/// Most scenery — trees, bushes, mushrooms, ore rocks, beehives — has no entry in the game's own
/// localization, because a sighted player never needs one: they can see it. For us those objects
/// fell through to the raw id with the underscores swapped for spaces ("Bush 2 berry",
/// "Marble heap mid 1", "Tree 3 2 bees"), which is both English and gibberish.
///
/// Naming every id individually is hopeless — there are thousands, and they multiply with every
/// variant suffix. But the ids are systematic: the id CONTAINS the words that describe the thing
/// ("bush_2_berry" is a berry bush, "steep_iron" is an iron deposit). So instead of a per-id table
/// this is a small ordered list of substring rules, each pointing at a lang key. ~40 rules cover
/// the scenery a player actually walks past.
///
/// Order matters and is the whole trick: the list runs MOST SPECIFIC FIRST, so "bush_2_berry" is
/// claimed by the "berry" rule before the plain "bush" rule can take it, and a beehive on a tree
/// ("tree_3_2_bees") is claimed by "bees" before "tree". This mirrors the ordering that
/// <see cref="ObjectNavigator"/> already uses to sort the same objects into categories.
///
/// Each rule yields a COMPLETE phrase from the lang file ("Beerenbusch", "Großer Baum"), never an
/// adjective glued onto a noun at runtime — German adjective endings depend on the noun's gender,
/// so composing them in code would produce "großer Busch" where it should be "großer Baum" but
/// "große Eiche". Whole phrases keep that decision with the translator.
///
/// This only ever runs when the game itself has no name for the object, so a real translation
/// always wins (see <see cref="InteractionDetector.LocalizedObjectName"/>).
/// </summary>
internal static class DescriptiveNames
{
    // (substring to look for in the obj_id, lang key holding the spoken phrase).
    // Keep most-specific rules above the general ones they'd otherwise be swallowed by.
    private static readonly (string Match, string Key)[] Rules =
    {
        // --- beehives (before "tree": hives live on trees and carry a tree id) ------------
        ("bees_done",   "obj.beehive_ready"),
        ("bees",        "obj.beehive_tree"),
        ("beehouse",    "obj.beehive"),
        ("hive",        "obj.beehive"),

        // --- trees ------------------------------------------------------------------------
        ("stump",       "obj.tree_stump"),
        ("tree_apple",  "obj.tree_apple"),
        ("tree_big",    "obj.tree_big"),
        ("tree_dry",    "obj.tree_dry"),
        ("dry_tree",    "obj.tree_dry"),
        ("tree",        "obj.tree"),

        // --- bushes -----------------------------------------------------------------------
        ("bush_berry",  "obj.bush_berry"),
        ("berry",       "obj.bush_berry"),
        ("bush",        "obj.bush"),

        // --- mushrooms --------------------------------------------------------------------
        ("toadstool",       "obj.mushroom_poison"),
        ("mushroom_poison", "obj.mushroom_poison"),
        ("mushroom",        "obj.mushroom"),

        // --- flowers / small gatherables --------------------------------------------------
        ("flower",      "obj.flower"),
        ("herb",        "obj.herb"),
        ("branch",      "obj.branch"),
        ("wheat",       "obj.wheat"),
        ("sand",        "obj.sand"),
        ("clay",        "obj.clay"),

        // --- ore / mining deposits (before the generic stone rules) -----------------------
        ("iron_ore",    "obj.ore_iron"),
        ("steep_iron",  "obj.ore_iron"),
        ("gold_ore",    "obj.ore_gold"),
        ("steep_gold",  "obj.ore_gold"),
        ("silver_ore",  "obj.ore_silver"),
        ("coal",        "obj.coal"),
        ("ore",         "obj.ore"),

        // --- stone ------------------------------------------------------------------------
        ("marble",      "obj.marble"),
        ("granite",     "obj.granite"),
        ("boulder",     "obj.boulder"),
        ("rock",        "obj.rock"),
        ("stone",       "obj.stone"),

        // --- fishing spots (river/waterfall/sea decide which fish bite, so keep them apart) --
        ("waterfall_fishing", "obj.fishing_waterfall"),
        ("river_fishing",     "obj.fishing_river"),
        ("sea_fishing",       "obj.fishing_sea"),
        ("lake_fishing",      "obj.fishing_lake"),
        ("fishing_spot",      "obj.fishing_spot"),

        // --- misc scenery the game leaves unnamed ------------------------------------------
        // --- dungeon / world enemies ------------------------------------------------------
        // "worker_zombie" MUST stay above the plain "zombie" rule: our own workers are not mobs.
        ("worker_zombie",   "obj.worker_zombie"),
        ("slime",           "obj.mob_slime"),
        ("skeleton",        "obj.mob_skeleton"),
        ("bat",             "obj.mob_bat"),
        ("spider",          "obj.mob_spider"),
        ("rat",             "obj.mob_rat"),
        ("ghost",           "obj.mob_ghost"),
        ("golem",           "obj.mob_golem"),
        ("wolf",            "obj.mob_wolf"),
        ("boar",            "obj.mob_boar"),
        ("snake",           "obj.mob_snake"),
        ("worm",            "obj.mob_worm"),
        ("beetle",          "obj.mob_beetle"),
        ("mole",            "obj.mob_mole"),
        ("demon",           "obj.mob_demon"),
        ("mummy",           "obj.mob_mummy"),
        ("zombie",          "obj.mob_zombie"),

        ("donat_box",       "obj.donation_box"),
        ("tavern_cashbox",  "obj.cashbox"),
        ("writers_table",   "obj.writers_table"),
        ("nameplate",       "obj.nameplate"),
        ("garden_carrot",   "obj.garden_carrot"),
        ("bat_test",        "obj.bat"),
        ("citizen_woman",   "obj.citizen_woman"),
        ("citizen_man",     "obj.citizen_man"),
        ("citizen",         "obj.citizen"),
        ("guard",           "obj.guard"),
        ("water_well",    "obj.well"),
        ("hiccup_grass",  "obj.hiccup_grass"),
        ("ground_shit",   "obj.dung"),
        ("blockage",      "obj.blockage"),
        ("roof",          "obj.roof"),
    };

    // Zone ids, used for the Landmarks list. Separate from the object rules because the same word
    // means something else at zone scale: "tree_garden" is an orchard, not a tree.
    // Only consulted when the GAME has no name for the zone (see ObjectNavigator.ZoneLabel).
    private static readonly (string Match, string Key)[] ZoneRules =
    {
        ("tree_garden", "zone.tree_garden"),
        ("beegarden",   "zone.beegarden"),
        ("bee_garden",  "zone.beegarden"),
        ("vegetable",   "zone.vegetable_garden"),
        ("garden",      "zone.garden"),
        ("mf_wood",     "zone.woodshed"),
        ("sawmill",     "zone.sawmill"),
        ("cemetery",    "zone.graveyard"),
        ("graveyard",   "zone.graveyard"),
        ("church",      "zone.church"),
        ("forest",      "zone.forest"),
        ("mountain",    "zone.mountains"),
        ("swamp",       "zone.swamp"),
        ("river",       "zone.river"),
        ("village",     "zone.village"),
        ("town",        "zone.town"),
        ("farm",        "zone.farm"),
        ("camp",        "zone.camp"),
    };

    /// <summary>A descriptive name for an untranslated ZONE id, or null when no rule matches.</summary>
    internal static string ForZone(string zoneId)
    {
        return Match(zoneId, ZoneRules);
    }

    /// <summary>
    /// A descriptive phrase for an untranslated obj_id, or null when no rule matches (the caller
    /// then falls back to the prettified id, as before).
    /// </summary>
    internal static string For(string objId)
    {
        return Match(objId, Rules);
    }

    /// <summary>
    /// First rule that matches <paramref name="id"/>, resolved to its text.
    ///
    /// Matching is on WHOLE UNDERSCORE-SEPARATED WORDS, not raw substrings: both the id and the
    /// pattern are normalised to "_word_word_" form and compared with their underscores intact.
    /// A plain substring test looked fine until you notice what the short rules catch — "ore"
    /// inside "store_box", "rock" inside "crockery", "hive" inside "archive", "stone" inside
    /// "hearthstone". Word matching kills that whole class of mislabelling while still letting a
    /// rule span several words ("tree_apple", "bees_done").
    /// </summary>
    private static string Match(string id, (string Match, string Key)[] rules)
    {
        if (string.IsNullOrEmpty(id)) return null;

        var haystack = Normalize(id);
        foreach (var (match, key) in rules)
        {
            if (haystack.IndexOf(Normalize(match), StringComparison.Ordinal) < 0) continue;
            // A rule whose key is missing from the lang file shouldn't swallow the object into
            // a spoken key name — treat it as no match and keep looking.
            var text = Loc.Find(key);
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return null;
    }

    /// <summary>
    /// "Marble heap mid 1" / "tree_3_2 (Clone)" -> "_marble_heap_mid_1_" / "_tree_3_2_clone_".
    /// Anything that isn't a letter or digit becomes a separator, so ids, prefab names and the
    /// space-separated place words from a door tag all match the same rules.
    /// </summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        sb.Append('_');
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb[sb.Length - 1] != '_') sb.Append('_');
        }
        if (sb[sb.Length - 1] != '_') sb.Append('_');
        return sb.ToString();
    }

    // Destination words recovered from a teleport door's custom_tag ("tp_tavern_from_cellar_b_").
    // These are English words baked into the tag, not ids the game translates, so the same
    // substring-to-lang-key idea applies. Longest/most specific first for the same reason.
    private static readonly (string Match, string Key)[] PlaceRules =
    {
        ("tavern_cellar", "place.tavern_cellar"),
        ("cellar",        "place.cellar"),
        ("tavern",        "place.tavern"),
        ("church",        "place.church"),
        ("mortuary",      "place.mortuary"),
        ("morgue",        "place.mortuary"),
        ("basement",      "place.basement"),
        ("dungeon",       "place.dungeon"),
        ("house",         "place.house"),
        ("home",          "place.home"),
        ("shop",          "place.shop"),
        ("barn",          "place.barn"),
        ("mine",          "place.mine"),
        ("inn",           "place.inn"),
        ("hut",           "place.hut"),
        ("tower",         "place.tower"),
        ("castle",        "place.castle"),
        ("garden",        "place.garden"),
        ("workshop",      "place.workshop"),
        ("alarich",       "place.alarich_tent"),
        ("alerich",       "place.alarich_tent"),
        ("witch",         "place.witch"),
        ("camp",          "place.camp"),
    };

    /// <summary>Translated name for a door destination, or null to keep the raw place words.</summary>
    internal static string ForPlace(string place)
    {
        if (string.IsNullOrEmpty(place)) return null;

        return Match(place, PlaceRules);
    }
}
