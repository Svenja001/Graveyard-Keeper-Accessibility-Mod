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
        // --- named spots the game left untranslated -----------------------------------
        // The river bank you throw a corpse off. The game has no name for it in any language, so
        // walking up to it announced the raw id ("throw_body_river") even after the navigator
        // learned the name. Same phrase here, so approaching it and browsing to it agree.
        ("throw_body_river", "landmark.river_body_throw"),

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

        // --- vegetable beds ---------------------------------------------------------------
        // Sits BELOW trees/bushes (so "bush_berry_garden" stays a berry bush) but ABOVE the loose
        // crop rules below (so "garden_wheat" is a bed, not a wheat plant).
        // "garden_of_stones" is a graveyard decoration, NOT a bed - it must beat the generic
        // "garden" rule at the bottom of this block.
        ("garden_of_stones", "obj.garden_of_stones"),
        ("garden_carrot",    "obj.garden_carrot"),
        ("garden_beet",      "obj.garden_beet"),
        ("garden_cabbage",   "obj.garden_cabbage"),
        ("garden_cannabis",  "obj.garden_cannabis"),
        ("garden_lentils",   "obj.garden_lentils"),
        ("garden_onion",     "obj.garden_onion"),
        ("garden_pumpkin",   "obj.garden_pumpkin"),
        // Confirmed from the log: the real id is SINGULAR ("garden_hop", "garden_hop_growing",
        // "garden_hop_planting_"). The plural spelling is kept as a cheap guard for variants.
        ("garden_hop",       "obj.garden_hops"),
        ("garden_hops",      "obj.garden_hops"),
        ("garden_wheat",     "obj.garden_wheat"),
        ("garden_flax",      "obj.garden_flax"),
        ("garden_grapes",    "obj.garden_grapes"),
        ("garden",           "obj.garden_bed"),

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
        // Above "stone": the smithy's decorative stone pile reads as "stone" otherwise, and it is
        // a decoration you place, not a resource you mine.
        ("mf_stones",   "obj.decor_stones"),
        ("stone",       "obj.stone"),

        // --- fishing spots (river/waterfall/sea decide which fish bite, so keep them apart) --
        ("waterfall_fishing", "obj.fishing_waterfall"),
        ("river_fishing",     "obj.fishing_river"),
        ("sea_fishing",       "obj.fishing_sea"),
        ("lake_fishing",      "obj.fishing_lake"),
        ("fishing_spot",      "obj.fishing_spot"),

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

        // --- camp / ruins scenery ---------------------------------------------------------
        ("camp_wagon",     "obj.camp_wagon"),
        ("tent",           "obj.tent"),
        ("chicken",        "obj.chicken"),
        ("ruins_pillar",   "obj.ruins_pillar"),
        ("ruins_viaduct",  "obj.ruins_viaduct"),
        ("ruins",          "obj.ruins"),

        // --- misc scenery the game leaves unnamed ------------------------------------------
        ("donat_box",      "obj.donation_box"),
        ("tavern_cashbox", "obj.cashbox"),
        ("writers_table",  "obj.writers_table"),
        ("nameplate",      "obj.nameplate"),
        ("cupboard",       "obj.cupboard"),
        ("fence_wood",     "obj.fence_wood"),
        ("lantern",        "obj.lantern"),
        ("sword_rack",     "obj.sword_rack"),
        ("worker_invisible", "obj.invisible_worker"),
        ("citizen_woman",  "obj.citizen_woman"),
        ("citizen_man",    "obj.citizen_man"),
        ("citizen",        "obj.citizen"),
        ("guard",          "obj.guard"),
        ("water_well",     "obj.well"),
        ("hiccup_grass",   "obj.hiccup_grass"),
        ("ground_shit",    "obj.dung"),
        ("pile_on_ground", "obj.dirt_pile"),
        ("blockage",       "obj.blockage"),
        ("obstruction",    "obj.blockage"),
        ("roof",           "obj.roof"),

        // --- ids the [NAMES] log caught speaking raw ---------------------------------------
        // Everything here comes from a play session's "No translation and no rule for '<id>'"
        // lines, so each entry is an id the game really does leave unnamed. Kept last so the
        // rules above, which were written against known ids, keep their precedence.
        ("wall_candelabrum", "obj.candelabrum_wall"),
        ("candelabrum",      "obj.candelabrum"),
        ("church_candle",    "obj.church_candle"),
        ("candle",           "obj.candle"),
        ("spiral_stair",     "obj.spiral_stairs"),
        ("vine_press",       "obj.vine_press"),
        // The entrance object, not a teleport door - but it is the same thing to the player,
        // so it borrows the door wording instead of getting a second phrasing of its own.
        ("dungeon_enter",    "door.dungeon_entrance"),
        ("hatch",            "door.hatch"),
        ("grille_opened",    "obj.grille_open"),
        ("grille",           "obj.grille"),
        ("smilers_box",      "obj.smiler_box"),
        ("elevator",         "obj.elevator"),
        // Above the bare "empty" exact rule: an empty grave is a grave, not a blank slot.
        ("grave_empty",      "obj.grave_empty"),
        ("grave_ground",     "obj.grave_ground"),
        // The buried-body stage of a grave. Reuses the phrase ObjectNavigator already speaks for
        // it, which is the game's own grave_body_hdr wording, so the tracker and a walk-up agree.
        ("grave_corp",       "grave.with_body"),
        // The corpse hatches, in/out, above the plain "morgue" rule for the building itself. The
        // game only ever names their BROKEN variants (morgue_throw_in_broken / _out_broken), so the
        // working ones arrive here unnamed; each language borrows what the game calls the broken
        // one. "in" is the outside hatch you drop a body into, "out" the inside one you clear it
        // through - which is the opposite of what the ids sound like, hence the explicit wording.
        ("morgue_throw_in",  "obj.morgue_hatch_outside"),
        ("morgue_throw_out", "obj.morgue_hatch_inside"),
        ("morgue",           "obj.morgue"),
        // Where corpses are burnt. The game names only the placement hint (mf_pyre_placed,
        // "Place for burning corpses"), never the object, so both the built pyre and its burnt-out
        // remains land here.
        ("pyre",             "obj.pyre"),
        ("bracken",          "obj.fern"),
        ("hops",             "obj.hops"),
        ("village_wc",       "obj.outhouse"),
        ("church_visitor",   "obj.church_visitor"),
        // An invisible manager object that parks NPCs on standing spots; it has no body, but
        // it is in the world list, so say what it is rather than "Idle points stock".
        ("idle_points",      "obj.idle_point_marker"),
        ("wood_obstacle",    "obj.wood_obstacle"),
        ("obstacle",         "obj.obstacle"),
        // Below "sword_rack" (above), which would otherwise lose its weapons to this.
        ("rack",             "obj.rack"),
        ("vase",             "obj.vase"),

        // ---- Objects the game itself never names -------------------------------------------
        // Collected from a play session's "[NAMES] No translation and no rule for …" lines, which
        // is what the mod logs when it has to read a raw id aloud. Each entry deliberately matches
        // the FAMILY rather than the individual id, so the six piles of broken glass, the three
        // broken barrels and the five tavern guests are one name apiece rather than a numbered
        // parade. Placement in this list is load-bearing — see the notes on the tighter ones.

        // Buildings. "church" sits below church_candle and church_visitor above, which would
        // otherwise be swallowed by it; "village_henhouse" must stay above the generic "house".
        ("smithy",             "obj.smithy"),
        ("sawmill",            "obj.sawmill"),
        ("village_henhouse",   "obj.henhouse"),
        ("village_hut",        "obj.village_hut"),
        ("storage",            "obj.storage_shed"),
        ("church",             "obj.church"),
        ("wall_cellar",        "obj.cellar_wall"),
        ("big_broken_bridge",  "obj.broken_bridge"),
        ("turnpike",           "obj.turnpike"),
        ("gate_wood",          "obj.wooden_gate"),

        // Props and furniture. "pumpkin" sits below garden_pumpkin (the crop) far above, and
        // "cashbox" below tavern_cashbox, so each keeps the more specific name where it applies.
        ("campfire",           "obj.campfire"),
        // Its own name, not obj.cashbox: that one reads "Tavern cashbox" and belongs to the rule
        // for tavern_cashbox above. This is the bare one found elsewhere.
        ("cashbox",            "obj.cashbox_plain"),
        ("flour_bag",          "obj.flour_bag"),
        ("pumpkin",            "obj.pumpkin"),
        ("tavern_table",       "obj.tavern_table"),
        ("table_cultist",      "obj.cultist_table"),
        ("old_wood_swamp",     "obj.rotten_wood"),
        ("witch_pylon",        "obj.witch_pyre"),
        ("mf_wood_panel",      "obj.wood_panel"),
        ("mf_anvil",           "obj.decor_anvil"),

        // Smashed remains. Above the plain "barrel" family so the barrel wagon keeps its own name.
        ("camp_barrel_wagon",  "obj.barrel_wagon"),
        ("barrel0",            "obj.broken_barrel"),
        ("pile_of_broken_glass", "obj.broken_glass"),
        ("bookcase",           "obj.broken_bookcase"),
        ("dungeon_obj_chair",  "obj.broken_chair"),
        ("dungeon_obj_bench",  "obj.broken_bench"),

        // People the game leaves unnamed.
        ("npc_tavern_visitor", "obj.tavern_guest"),
        ("npc_satyr",          "obj.satyr"),
        ("npc_lilya",          "obj.lilya"),
        ("stranger",           "obj.stranger"),

        ("house",            "obj.house"),
    };

    // Ids named by the WHOLE id rather than by a word inside it, because the word is far too
    // generic to use as a substring: the tavern building's id is just "Tavern", but
    // "tavern_chair" is a chair; a blank decoration slot is "empty", but "grave_empty" is a
    // grave. Checked before <see cref="Rules"/>.
    private static readonly Dictionary<string, string> ExactRules = new(StringComparer.OrdinalIgnoreCase)
    {
        { "tavern", "obj.tavern" },
        { "empty",  "obj.empty_slot" },
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

    /// <summary>
    /// Splits INSIDE a resource group, where one group holds nodes that pay out differently.
    ///
    /// <see cref="GroupNames"/> follows the game's own grouping, which is right for the question
    /// "may I work this yet" and wrong for "is walking over there worth it". The wood groups are
    /// the case: t_wood_small holds the four <c>tree_tiny_*</c> ids, which drop nothing but sticks,
    /// alongside <c>tree_1_*</c> and <c>tree_2_*</c>, which drop logs. Both were "Small tree", so a
    /// player who needed wood had a list of a dozen identical entries and no way to tell the
    /// stick-only ones apart except by chopping one. Naming the three tiers apart — small tree
    /// (sticks), tree (a log), big tree (two) — gives the same information a sighted player reads
    /// off the silhouette.
    ///
    /// Checked BEFORE the group name, and matched as whole underscore-separated words like every
    /// other rule list here, so an entry only ever narrows a group, never widens one.
    /// </summary>
    private static readonly (string Match, string Key)[] NodeRules =
    {
        ("tree_tiny", "obj.tree_small"),
    };

    /// <summary>
    /// Names taken from the game's own RESOURCE GROUPS, which split the scenery exactly where the
    /// player needs it split.
    ///
    /// The substring rules above name a whole family at once — every tree is "Tree" — and that is
    /// right for scenery you only need to recognise. It is wrong for the nodes you WORK, because
    /// the game gates them by group: small trees and big trees are separate unlocks
    /// (t_wood_small / t_wood_big, "The idea of the tree" and "Woodcutter"), as are the two
    /// mushrooms and the two grades of iron. A player hearing "Tree" twelve times cannot tell which
    /// ones they are allowed to fell, and finds out only by walking there and being refused.
    ///
    /// Keyed on the group id rather than on object ids, so it follows the game's own grouping: the
    /// 15 big-tree ids and the 14 small-tree ids stay correct without listing one of them here, and
    /// a node the balance moves between groups moves with it.
    ///
    /// Only groups whose family name is ambiguous need an entry; where the family rules above
    /// already say the right thing (a berry bush is "Berry bush" either way), leaving the group out
    /// keeps the existing name.
    /// </summary>
    private static readonly Dictionary<string, string> GroupNames = new(StringComparer.Ordinal)
    {
        { "t_wood_small", "obj.tree" },
        { "t_wood_big",   "obj.tree_large" },
        { "t_mushroom",   "obj.mushroom_edible" },
        { "t_mushroom2",  "obj.mushroom_red" },
        { "t_iron_ore_1", "obj.ore_iron_surface" },
        { "t_iron_ore_2", "obj.ore_iron_vein" },
    };

    /// <summary>
    /// The name for this object's resource group, or null when the group has no name of its own and
    /// the caller should keep the family name.
    /// </summary>
    internal static string ForGroup(ObjectDefinition def)
    {
        try
        {
            var groups = def?.object_groups;
            if (groups == null || groups.Count == 0) return null;

            foreach (var group in groups)
            {
                if (group?.id == null) continue;
                if (!GroupNames.TryGetValue(group.id, out var key)) continue;
                var text = Loc.Find(key);
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Family names that are too vague to be a name, where the node's own DROP says what it is.
    ///
    /// Flowers are the case this exists for. The game has nine of them, flower_small_1 through _9,
    /// and every one is "Blume" by the family rule — but they are three different alchemy
    /// ingredients in rotation (1/4/7 dandelion, 2/5/8 chamomile, 3/6/9 poppy), and a player
    /// picking them wants to know which before they bend down. They have no object group, so
    /// <see cref="ForGroup"/> cannot help; what distinguishes them is exactly what falls out, and
    /// the game already names that item properly ("Gelbe Blume", "Weiße Blume", "Rote Blume").
    ///
    /// Keyed on the lang key the family rule produced, not on the id, so this can never steal a
    /// more specific name: an apple tree in blossom (decor_tree_apple_1_flower) is claimed by the
    /// "tree_apple" rule long before "flower", so it keeps being an apple tree.
    /// </summary>
    private static readonly HashSet<string> DropNamedKeys = new(StringComparer.Ordinal)
    {
        "obj.flower",
    };

    /// <summary>
    /// The best name for a resource node: the tier inside its work group where the group lumps
    /// unlike nodes together (<see cref="NodeRules"/>), else the work group where that splits the
    /// family (<see cref="ForGroup"/>), else what it drops where the family name says too little.
    /// Null when the ordinary family name is the right answer.
    /// </summary>
    internal static string ForNode(WorldGameObject wgo)
    {
        try
        {
            var def = wgo?.obj_def;
            if (def == null) return null;

            var id = def.id;
            if (string.IsNullOrEmpty(id)) id = wgo.obj_id;

            var narrowed = Match(id, NodeRules);
            if (!string.IsNullOrEmpty(narrowed)) return narrowed;

            var group = ForGroup(def);
            if (!string.IsNullOrEmpty(group)) return group;

            var key = MatchKey(id, Rules);
            if (key == null || !DropNamedKeys.Contains(key)) return null;

            return ResourceYield.SingleDropName(wgo);
        }
        catch
        {
            return null;
        }
    }

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
        if (!string.IsNullOrEmpty(objId)
            && ExactRules.TryGetValue(objId, out var exactKey))
        {
            var exact = Loc.Find(exactKey);
            if (!string.IsNullOrEmpty(exact)) return exact;
        }

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
        var key = MatchKey(id, rules);
        return key == null ? null : Loc.Find(key);
    }

    /// <summary>
    /// The lang KEY of the first rule that matches, rather than its text. Lets a caller ask which
    /// family claimed an id — <see cref="ForNode"/> needs that to know when a family name is too
    /// vague to stand on its own.
    /// </summary>
    private static string MatchKey(string id, (string Match, string Key)[] rules)
    {
        if (string.IsNullOrEmpty(id)) return null;

        var haystack = Normalize(id);
        foreach (var (match, key) in rules)
        {
            if (haystack.IndexOf(Normalize(match), StringComparison.Ordinal) < 0) continue;
            // A rule whose key is missing from the lang file shouldn't swallow the object into
            // a spoken key name — treat it as no match and keep looking.
            if (!string.IsNullOrEmpty(Loc.Find(key))) return key;
        }
        return null;
    }

    /// <summary>
    /// "Marble heap mid 1" / "tree_3_2 (Clone)" -> "_marble_heap_mid_1_" / "_tree_3_2_clone_".
    /// Anything that isn't a letter or digit becomes a separator, so ids, prefab names and the
    /// space-separated place words from a door tag all match the same rules.
    ///
    /// A digit stuck straight onto a word is separated too ("dungeon_obj_rack02" ->
    /// "_dungeon_obj_rack_02_"), because the variant number is part of the id's spelling and not
    /// part of the word: without the split, "rack02" is one token and the "rack" rule misses it.
    /// The rule patterns run through the same normalisation, so this only ever finds MORE
    /// matches - no pattern contains a digit to be broken up.
    /// </summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        sb.Append('_');
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                var prev = sb[sb.Length - 1];
                if (prev != '_' && char.IsDigit(c) != char.IsDigit(prev)) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
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
