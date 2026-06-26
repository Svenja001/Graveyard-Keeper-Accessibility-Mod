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

    /// <summary>
    /// The skull score of a single body part (flesh, bones, blood, organs, skull) as shown in
    /// the autopsy grid — e.g. "2 white" (raises grave value) or "1 red" (lowers it). Each part
    /// carries its own red (<see cref="Item.GetRedSkullsValue"/> = q_minus) and white
    /// (<see cref="Item.GetWhiteSkullsValue"/> = q_plus) values, which is what makes one part
    /// "good" and another "bad". Returns null for non-part items, "no skull value" for a part
    /// that contributes nothing.
    /// </summary>
    internal static string DescribePart(Item part)
    {
        if (part == null || part.definition == null || part.IsEmpty()) return null;

        var type = part.definition.type;
        if (type != ItemDefinition.ItemType.BodyUniversalPart
            && type != ItemDefinition.ItemType.BodyBodyPart
            && type != ItemDefinition.ItemType.SoulBodyPart)
            return null;

        try
        {
            int red = part.GetRedSkullsValue();     // q_minus — bad, lowers grave value
            int white = part.GetWhiteSkullsValue();  // q_plus  — good, raises grave value
            if (red == 0 && white == 0) return "no skull value";

            var bits = new List<string>();
            if (red != 0) bits.Add($"{red} red");
            if (white != 0) bits.Add($"{white} white");
            return string.Join(", ", bits);
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
