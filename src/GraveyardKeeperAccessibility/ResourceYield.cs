namespace GraveyardKeeperAccessibility;

/// <summary>
/// What a resource node is CERTAIN to drop — used to name it, never to spoil it.
///
/// This once spoke the yield out loud ("Tree, gives 2 wood, 3 sticks"). That was removed on the
/// player's own reasoning, and it is the right reasoning: a sighted player does not know what a
/// tree holds before they fell it either, and handing a screen reader user numbers the screen never
/// shows is not accessibility, it is a different game. What they cannot see is which tree it IS,
/// and that is what the mod says instead — see <see cref="DescriptiveNames.ForNode"/> and
/// <see cref="WorkUnlock"/>.
///
/// What survives is the one place a drop genuinely IS the identity of the thing: the nine flowers
/// (flower_small_1…9), which are three alchemy ingredients in rotation with no name, no work group
/// and nothing else to tell them apart. The game names the bloom itself properly — "Gelbe Blume",
/// "Weiße Blume", "Rote Blume" — so the flower is named after it. A player who can see one knows
/// its colour at a glance; this is that glance, not a preview of the loot.
/// </summary>
internal static class ResourceYield
{
    private static ManualLogSource _log;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>
    /// The one thing this node is FOR, when it drops exactly one kind of item — which for a flower
    /// is a better name than its family ("Gelbe Blume", not "Blume"). Null when the node drops
    /// nothing certain, or several different things, in which case no single item speaks for it.
    /// <see cref="DescriptiveNames.ForNode"/> decides where a name like this is wanted.
    /// </summary>
    internal static string SingleDropName(WorldGameObject wgo)
    {
        try
        {
            var def = wgo?.obj_def;
            // The chances and amounts are expressions the game evaluates against the player, so
            // without one there is nothing to evaluate.
            if (def == null || MainGame.me?.player == null) return null;

            var entries = Guaranteed(def, wgo);
            return entries.Count == 1 ? entries[0] : null;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[YIELD] SingleDropName failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The distinct items that are CERTAIN to fall out of this node, in the order the definition
    /// lists them. Mirrors <c>ResModificator.ProcessItemsListBeforeDrop</c>, which is what the game
    /// runs when a node's HP hits zero.
    ///
    /// Only certainties count. A chance drop (the silver nugget in an iron vein, the butterfly on a
    /// flower, a perk-granted bonus log) is not what a node IS, and counting one would make a
    /// flower answer to two names depending on the roll.
    /// </summary>
    private static List<string> Guaranteed(ObjectDefinition def, WorldGameObject wgo)
    {
        var result = new List<string>();

        var drops = def.drop_items;
        if (drops == null || drops.Count == 0) return result;

        var player = MainGame.me?.player;

        foreach (var item in drops)
        {
            if (item == null || string.IsNullOrEmpty(item.id)) continue;
            if (item.is_tech_point) continue;               // r/g/b points, on nearly every node

            // Part of a weighted group: the game picks exactly ONE of the group at random, so none
            // of them is a promise.
            if (item.chance_group != -1) continue;

            // Certain, not merely likely. EvaluateFloat gives the probability whether it is a bare
            // number or an expression ("1" reads as 1.0), so this needs no special case for the
            // plain drops and still rejects the perk-gated bonus log.
            if (item.self_chance == null) continue;
            if (item.self_chance.EvaluateFloat(wgo, player) < 1f) continue;

            var name = ItemName(item);
            if (string.IsNullOrEmpty(name) || result.Contains(name)) continue;

            result.Add(name);
        }

        return result;
    }

    /// <summary>The item's own localized name ("Wood", "Yellow flower"), or null when it has none.</summary>
    private static string ItemName(Item item)
    {
        try
        {
            var name = ScreenReader.StripNguiCodes(item.definition?.GetItemName() ?? "").Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }
}
