namespace GraveyardKeeperAccessibility;

/// <summary>
/// Scans the entire loaded world for corpses when the player presses K and reads them out.
///
/// Unlike the navigator's Corpses category — which is distance-capped to ~60 tiles so it stays
/// a "what can I walk to" list — this answers the broader question "is there a corpse anywhere?".
/// It ignores the distance cap and reports every body in the scene: lying on the ground, sitting
/// in morgue storage (corpse_bed / corpse_fridge), on a prep / autopsy table, or interred in a
/// grave. It is the in-game equivalent of searching the save file for body items.
/// </summary>
internal static class CorpseScanner
{
    private const float TileSize = 96f;   // world units per tile (matches ObjectNavigator)

    private static ManualLogSource _log;

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

            var playerPos = MainGame.me.player?.pos ?? Vector2.zero;
            var finds = new List<(float dist, string text)>();

            // Bodies held by world objects: morgue beds/fridges and prep/autopsy tables. Graves
            // are skipped on purpose — an interred body isn't a loose corpse to process, and the
            // graveyard holds dozens of them which would bury the one delivery the player cares
            // about. Buried bodies remain findable via the Graves / Exhumable graves categories.
            foreach (var obj in UnityEngine.Object.FindObjectsOfType<WorldGameObject>(true))
            {
                if (obj == null || obj.is_removed) continue;

                // Skip DLC "ruins" the player doesn't own (souls zone, etc.) — uniform with the
                // proximity/navigation filters, even though these hold no body today.
                if (!ObjectNavigator.IsObjectDlcAvailable(obj)) continue;

                try
                {
                    if (obj.obj_def != null
                        && obj.obj_def.interaction_type == ObjectDefinition.InteractionType.Grave)
                        continue;
                }
                catch { }

                Item body = null;
                try { body = obj.GetBodyFromInventory(); } catch { }
                if (body == null || body.definition == null
                    || body.definition.type != ItemDefinition.ItemType.Body
                    || body.IsEmpty())
                    continue;

                var dist = Vector2.Distance(playerPos, obj.pos);
                finds.Add((dist, DescribeBody(body, LocationOf(obj), obj.pos, playerPos)));
            }

            // Bodies lying on the ground are DropResGameObjects, not WorldGameObjects.
            foreach (var drop in UnityEngine.Object.FindObjectsOfType<DropResGameObject>())
            {
                if (drop == null || drop.is_collected) continue;
                var res = drop.res;
                if (res == null || res.IsEmpty() || res.definition == null
                    || res.definition.type != ItemDefinition.ItemType.Body)
                    continue;

                var pos = (Vector2)drop.transform.position;
                var dist = Vector2.Distance(playerPos, pos);
                finds.Add((dist, DescribeBody(res, "on the ground", pos, playerPos)));
            }

            if (finds.Count == 0)
            {
                ScreenReader.Say("No corpse anywhere", interrupt: true);
                _log?.LogInfo("[CORPSE-SCAN] none found");
                return;
            }

            finds.Sort((a, b) => a.dist.CompareTo(b.dist));
            var lead = finds.Count == 1 ? "1 corpse" : $"{finds.Count} corpses";
            ScreenReader.Say($"{lead}. {string.Join(". ", finds.ConvertAll(f => f.text))}", interrupt: true);
            _log?.LogInfo($"[CORPSE-SCAN] {finds.Count} found");
        }
        catch (Exception ex)
        {
            _log?.LogError($"CorpseScanner error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>A spoken location for a corpse-holding object, derived from its obj_id.</summary>
    private static string LocationOf(WorldGameObject obj)
    {
        var id = obj.obj_id ?? "";
        if (id.StartsWith("corpse_bed") || id.StartsWith("corpse_fridge"))
            return "in morgue storage";
        if (id == "autopsi_table" || id.Contains("mf_preparation"))
            return "on a preparation table";

        // Graves keep a body in inventory; tell them apart from generic stations.
        try
        {
            if (obj.obj_def != null
                && obj.obj_def.interaction_type == ObjectDefinition.InteractionType.Grave)
                return "in a grave";
        }
        catch { }

        try
        {
            var label = InteractionDetector.GetObjectLabel(obj);
            if (!string.IsNullOrEmpty(label)) return $"on {label}";
        }
        catch { }
        return "stored";
    }

    private static string DescribeBody(Item body, string location, Vector2 pos, Vector2 playerPos)
    {
        var dist = Vector2.Distance(playerPos, pos);
        var dir = CompassDirection(playerPos, pos);
        var where = string.IsNullOrEmpty(dir) ? location : $"{location}, {dir}";
        var skulls = SkullInfo.Describe(body);
        var skullSuffix = string.IsNullOrEmpty(skulls) ? "" : $", {skulls}";
        return $"Corpse {where}, {dist / TileSize:F0} meters away{skullSuffix}";
    }

    private static string CompassDirection(Vector2 from, Vector2 to)
    {
        var d = to - from;
        if (d.sqrMagnitude < 1f) return "here";

        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        return (Mathf.RoundToInt(angle / 45f) % 8) switch
        {
            0 => "east",
            1 => "north-east",
            2 => "north",
            3 => "north-west",
            4 => "west",
            5 => "south-west",
            6 => "south",
            7 => "south-east",
            _ => "",
        };
    }
}
