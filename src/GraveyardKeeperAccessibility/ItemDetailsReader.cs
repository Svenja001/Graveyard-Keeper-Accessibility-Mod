namespace GraveyardKeeperAccessibility;

/// <summary>
/// "What is this, and what can I do with it?" for one item — spoken on demand with the D key in
/// any window, and automatically when Enter on an inventory item has no action of its own (a
/// resource like wood can't be used, equipped or opened, so Enter used to just re-read the name).
///
/// The game HAS an item-description system — <see cref="ItemDefinition.GetItemDescription"/> looks
/// up an "&lt;item_id&gt;_d" localization token — but the authors barely filled it in: of the 770
/// items in use, 631 have the token and only <b>78</b> have any text behind it, identically so in
/// all 11 shipped languages. So piping the game's own description into speech would stay silent for
/// nine items out of ten. That's why this reads the game's text where it exists and rebuilds the
/// rest from balance data, the same approach the tech tree already takes for unlock descriptions
/// with no text (see GUIAccessibility.UnlockFallbackText).
///
/// The one line that answers the player's actual question — "what do I do with this?" — is
/// <see cref="AppendUses"/>: every unlocked recipe that CONSUMES the item, named by what it makes.
/// 598 of the 770 items are an ingredient somewhere, and the game shows this nowhere at all (its
/// tooltip only tells you where an item is MADE, never what it is FOR).
/// </summary>
internal static class ItemDetailsReader
{
    /// <summary>How many products the "used for" line names before it summarizes the remainder.</summary>
    private const int MaxUses = 6;

    /// <summary>
    /// Item id (without any ":quality" suffix) -> every craft that consumes it. Built once per
    /// session from the balance data, which never changes at runtime; only the UNLOCK state of a
    /// craft changes, and that is re-checked on every read. Without the index, answering "what is
    /// this for" would mean walking all ~2700 craft definitions on every key press — exactly the
    /// kind of per-press sweep that caused the 0.1.2 stutter.
    /// </summary>
    private static Dictionary<string, List<CraftDefinition>> _consumers;

    /// <summary>
    /// The full spoken details block for an item, or null when there is no item to describe.
    /// Never returns an empty string: an item nothing is known about still gets its name plus a
    /// spoken "nothing more is known", so the key press is never silent.
    /// </summary>
    internal static string Describe(Item item)
    {
        try
        {
            if (item == null || item.IsEmpty()) return null;
            var def = item.definition;
            if (def == null) return null;

            var parts = new List<string>(8);

            var name = ScreenReader.StripNguiCodes(def.GetItemName() ?? "")?.Trim();
            if (string.IsNullOrEmpty(name)) name = item.id;
            if (string.IsNullOrEmpty(name)) return null;
            var tier = InventoryItemHandler.QualityTierName(def);
            parts.Add(string.IsNullOrEmpty(tier) ? name : $"{name}, {tier}");

            // The game's own tooltip text: the authored description for the few items that have
            // one, plus the lines it derives itself (effect on use, tool energy cost, tool
            // efficiency, sword damage, granted buffs, quality hints).
            var tooltip = TooltipText(def, item);
            if (!string.IsNullOrEmpty(tooltip)) parts.Add(tooltip);

            // Armour value is the one gear stat the game's own description leaves out.
            if (def.armor > 0f)
                parts.Add(Loc.Fmt("details.armor", Mathf.RoundToInt(def.armor)));

            AppendUses(parts, def, out var truncatedUses);

            // Where the item itself comes from — the tooltip's "crafted at" line.
            var madeAt = GUIAccessibility.CraftedAtText(def);
            if (!string.IsNullOrEmpty(madeAt)) parts.Add(madeAt);

            // Study state and, once studied, what it breaks down into at the alchemy bench. Item
            // cells already speak these on focus, but the details block is also reached from craft
            // recipe rows (whose label is the recipe, not the cell), so it repeats them to stay
            // self-contained.
            var study = InventoryItemHandler.DescribeStudyStatus(def, GUIAccessibility.IsStudyStationOpen());
            if (!string.IsNullOrEmpty(study)) parts.Add(study);

            var alchemy = InventoryItemHandler.DescribeAlchemyDecompose(def);
            if (!string.IsNullOrEmpty(alchemy)) parts.Add(alchemy);

            if (parts.Count == 1) parts.Add(Loc.Get("details.nothing_more"));

            // A capped list is no use to someone who needs the one entry that got cut. Say the
            // way out — and only when there is actually something more to hear.
            if (truncatedUses) parts.Add(Loc.Get("details.press_again"));

            return string.Join(". ", parts);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[DETAILS] describe failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The game's own tooltip body, cleaned for speech: newlines become sentence breaks, NGUI
    /// colour codes go, and the inline icon tokens ("25(b)", "(faith)") become words.
    /// </summary>
    /// <remarks>
    /// <see cref="ItemDefinition.GetItemDescription"/> dereferences a sermon's linked craft and a
    /// rat's buff items unguarded, so it can throw for oddly-configured items — hence the catch,
    /// mirroring GUIAccessibility.SafeItemDescription.
    /// </remarks>
    private static string TooltipText(ItemDefinition def, Item item)
    {
        string raw;
        try { raw = def.GetItemDescription(item); }
        catch { return null; }

        if (string.IsNullOrWhiteSpace(raw)) return null;

        // StripNguiCodes also turns the inline sprite tokens into words — "+(hp)3" into
        // "gives 3 health", "25(b)" into "25 blue points" — which is most of what a tooltip
        // written for someone who can see the icons actually says.
        var text = ScreenReader.StripNguiCodes(raw);

        // The tooltip stacks its lines with newlines; spoken, they need to read as sentences.
        text = text.Replace("\r", "").Replace("\n", ". ");
        // Item names inside a description are written in angle brackets ("use a <Keeper's Key>");
        // spoken, the brackets are noise, the name is not.
        text = text.Replace("<", "").Replace(">", "");
        text = Regex.Replace(text, @"\s{2,}", " ").Replace(" .", ".").Trim();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// The heart of this file: what the item is FOR. Adds up to two lines — the things it helps
    /// make ("used to make wooden planks, beams and 2 more") and the things it helps build ("used
    /// for building a beehive, a cellar"). Long lists are capped, and <paramref name="truncated"/>
    /// comes back true when something was left out so the caller can offer to read the rest.
    /// </summary>
    private static void AppendUses(List<string> parts, ItemDefinition def, out bool truncated)
    {
        CollectUses(def, out var products, out var buildings);
        truncated = products.Count > MaxUses || buildings.Count > MaxUses;

        if (products.Count > 0) parts.Add(Loc.Fmt("details.used_to_make", JoinCapped(products)));
        if (buildings.Count > 0) parts.Add(Loc.Fmt("details.used_to_build", JoinCapped(buildings)));
    }

    /// <summary>
    /// The same two lines with nothing left out — what the second O press reads. Null when the
    /// item has no capped list to expand, so the caller can fall back to repeating the summary
    /// rather than saying the same thing twice under a different name.
    /// </summary>
    internal static string DescribeAllUses(Item item)
    {
        try
        {
            var def = item?.definition;
            if (def == null) return null;

            CollectUses(def, out var products, out var buildings);
            if (products.Count <= MaxUses && buildings.Count <= MaxUses) return null;

            var parts = new List<string>(2);
            if (products.Count > 0)
                parts.Add(Loc.Fmt("details.used_to_make", string.Join(", ", products)));
            if (buildings.Count > 0)
                parts.Add(Loc.Fmt("details.used_to_build", string.Join(", ", buildings)));

            return parts.Count > 0 ? string.Join(". ", parts) : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[DETAILS] full use list failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Everything the item is an ingredient for, split into things you make and things you build.
    /// Only recipes the player has actually unlocked are listed, exactly as the game's own craft
    /// windows filter them, so this never spoils a recipe or sends anyone looking for a station
    /// they can't use yet.
    /// </summary>
    private static void CollectUses(ItemDefinition def, out List<string> products, out List<string> buildings)
    {
        products = new List<string>();
        buildings = new List<string>();

        if (!Consumers().TryGetValue(def.GetNameWithoutQualitySuffix(), out var crafts)) return;

        foreach (var craft in crafts)
        {
            if (craft == null || craft.hidden) continue;

            // Decomposing at the alchemy bench IS a use, but the "decomposes into powder, essence"
            // line says it better than "used to make green goo" ever could.
            if (craft.craft_type == CraftDefinition.CraftType.AlchemyDecompose) continue;

            // Studying an item at the research table is not making something, and every one of the
            // ~280 "surv:" crafts lists a story as its first output — on a CHANCE roll, which is
            // why the study table only sometimes hands one over. Taken at face value that made
            // "used to make: story" the single most common line in the whole mod, on 281 of the
            // 770 items, and it was wrong on every one of them. What studying gives is already
            // spoken by the study line further down.
            if (craft.craft_type == CraftDefinition.CraftType.Survey) continue;

            try { if (craft.IsLocked()) continue; }
            catch { /* no save loaded (main menu): treat as available rather than hiding it */ }

            if (craft is ObjectCraftDefinition objCraft)
            {
                var built = BuildingName(objCraft);
                if (!string.IsNullOrEmpty(built) && !buildings.Contains(built)) buildings.Add(built);
                continue;
            }

            var made = ProductName(craft);
            if (!string.IsNullOrEmpty(made) && !products.Contains(made)) products.Add(made);
        }
    }

    /// <summary>The localized name of what a craft produces, or null for one that makes no item
    /// (a survey, which pays only tech points).</summary>
    private static string ProductName(CraftDefinition craft)
    {
        try
        {
            // Prefer an output the craft always yields over one it only rolls for, so a recipe
            // whose bonus drop happens to be listed first isn't announced as the thing it makes.
            // Crafts whose ONLY outputs are chance-based (the zombie pulpit's stories) still name
            // that output — a maybe is all there is to say about them, and it is the truth.
            var output = FirstGuaranteedOutput(craft) ?? craft.GetFirstRealOutput();
            if (output == null) return null;

            var def = output.definition
                      ?? GameBalance.me.GetDataOrNull<ItemDefinition>(output.id);
            var name = ScreenReader.StripNguiCodes(def?.GetItemName() ?? "")?.Trim();
            // An id echoed back means the item has no translation — speaking it would just be a
            // raw token like "wood_balk_1".
            return string.IsNullOrEmpty(name) || name == def?.id ? null : name;
        }
        catch { return null; }
    }

    /// <summary>
    /// The craft's first output that is neither a tech point nor a chance roll, or null when every
    /// output is one or the other.
    /// </summary>
    private static Item FirstGuaranteedOutput(CraftDefinition craft)
    {
        if (craft.output == null) return null;
        foreach (var output in craft.output)
        {
            if (output == null || TechDefinition.TECH_POINTS.Contains(output.id)) continue;
            if (IsGuaranteedOutput(output)) return output;
        }
        return null;
    }

    /// <summary>
    /// True when the craft yields this output every single time. A <c>chance_group</c> means the
    /// game picks one member of the group at random; a chance expression means it rolls against
    /// the player's skill or luck; a flat probability below 1 speaks for itself.
    /// </summary>
    private static bool IsGuaranteedOutput(Item output)
    {
        try
        {
            if (output.chance_group >= 0) return false;

            var chance = output.self_chance;
            if (chance == null) return true;
            if (chance.has_expression) return false;

            return chance.EvaluateFloat() >= 0.999f;
        }
        catch { return true; }
    }

    /// <summary>The localized name of the object a build craft puts down, or null.</summary>
    private static string BuildingName(ObjectCraftDefinition craft)
    {
        try
        {
            // Demolition ("_remove_") and the "None" build type consume nothing worth naming.
            if (craft.build_type != ObjectCraftDefinition.BuildType.Put) return null;
            var objId = craft.out_obj;
            if (string.IsNullOrEmpty(objId)) return null;

            var name = ScreenReader.StripNguiCodes(GJL.L(objId) ?? "")?.Trim();
            if (!string.IsNullOrEmpty(name) && name != objId) return name;

            // No translation of its own — the mod's descriptive naming covers a lot of those.
            return DescriptiveNames.For(objId);
        }
        catch { return null; }
    }

    /// <summary>"planks, beams, boards" — or "planks, ... , boards and 4 more" past the cap, so a
    /// staple like wood doesn't turn into a minute-long list.</summary>
    private static string JoinCapped(List<string> names)
    {
        if (names.Count <= MaxUses) return string.Join(", ", names);
        var head = string.Join(", ", names.GetRange(0, MaxUses));
        return Loc.Fmt("details.and_more", head, names.Count - MaxUses);
    }

    /// <summary>The lazily built ingredient -> crafts index. Empty (never null) if the balance
    /// data isn't loaded yet.</summary>
    private static Dictionary<string, List<CraftDefinition>> Consumers()
    {
        if (_consumers != null) return _consumers;

        var map = new Dictionary<string, List<CraftDefinition>>(512);
        try
        {
            var balance = GameBalance.me;
            if (balance == null) return map;   // not cached: try again once the game has loaded

            foreach (var craft in balance.craft_data) Index(map, craft);
            foreach (var craft in balance.craft_obj_data) Index(map, craft);

            Plugin.Log.LogInfo($"[DETAILS] indexed {map.Count} ingredient(s) across "
                               + $"{balance.craft_data.Count + balance.craft_obj_data.Count} craft(s)");
            _consumers = map;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[DETAILS] could not index crafts: {ex.Message}");
        }
        return map;
    }

    /// <summary>File one craft under every item it consumes.</summary>
    private static void Index(Dictionary<string, List<CraftDefinition>> map, CraftDefinition craft)
    {
        if (craft?.needs == null) return;
        foreach (var need in craft.needs)
        {
            if (string.IsNullOrEmpty(need?.id)) continue;
            // Recipes name a specific quality ("wood:2"); the player is holding an item whose
            // definition id carries the same suffix, so both sides are indexed on the base id.
            var key = ItemDefinition.StaticGetNameWithoutQualitySuffix(need.id);
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<CraftDefinition>(4);
            if (!list.Contains(craft)) list.Add(craft);
        }
    }
}
