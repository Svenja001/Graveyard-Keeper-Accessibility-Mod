namespace GraveyardKeeperAccessibility;

/// <summary>
/// Reads the red/white skull values off a body item. Skulls live on bodies
/// (corpses, organs), not on objects in general: red = q_minus (Item.GetRedSkullsValue),
/// white = q_plus (Item.GetWhiteSkullsValue), and the body's durability is its freshness.
/// <see cref="Item.GetBodySkulls"/> sums the body plus everything in its inventory
/// (injected organs, embalming items) the same way the game's skull bar does, and reports
/// how many white skulls are actually usable at the current decay level.
/// </summary>
internal static class SkullInfo
{
    /// <summary>The body item backing a navigator target (a grave's interred body or a
    /// ground corpse), or null if the target isn't / doesn't hold a body.</summary>
    internal static Item GetBodyItem(NavigationTarget target)
    {
        try
        {
            if (target.Object != null)
            {
                var body = target.Object.GetBodyFromInventory();
                if (IsBody(body)) return body;
            }

            if (target.IsDrop && target.DropGo != null)
            {
                var drop = target.DropGo.GetComponent<DropResGameObject>();
                var res = drop != null ? drop.res : null;
                if (IsBody(res)) return res;
            }
        }
        catch { }
        return null;
    }

    /// <summary>e.g. "2 red, 4 white, 80 percent fresh" — or null if not a body.</summary>
    internal static string Describe(Item body)
    {
        if (!IsBody(body)) return null;
        try
        {
            body.GetBodySkulls(out int red, out int white, out int whiteAvailable);
            int fresh = Mathf.CeilToInt(body.durability * 100f);

            var core = $"{red} red, {white} white";
            // Decay caps how many white skulls actually count toward grave value.
            if (whiteAvailable < white)
                core += $", {whiteAvailable} white usable";
            return $"{core}, {fresh} percent fresh";
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBody(Item item)
    {
        return item != null
            && item.definition != null
            && item.definition.type == ItemDefinition.ItemType.Body
            && !item.IsEmpty();
    }
}
