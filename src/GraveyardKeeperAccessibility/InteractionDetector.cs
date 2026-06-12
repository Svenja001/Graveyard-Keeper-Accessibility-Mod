namespace GraveyardKeeperAccessibility;

internal static class InteractionDetector
{
    private static string _lastAnnouncedObject = null;
    private static int _lastHighlightedDropId = 0;
    private static bool _wasCarrying = false;
    private static bool _wasCrafting = false;
    private static ManualLogSource _log;
    private static bool _initialized = false;
    private const float InteractionRange = 300f;

    /// <summary>
    /// True while a station next to the player is mid-craft. Station crafts (e.g. the autopsy
    /// table cutting flesh) are timed and the player performs them by standing put — walking
    /// away cancels them and can wedge the station. The navigator reads this to refuse
    /// auto-walk until the craft finishes. Updated each frame by <see cref="AnnounceCraftState"/>.
    /// </summary>
    internal static bool IsPlayerCrafting => _wasCrafting;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _log?.LogInfo("[INTERACTION] InteractionDetector initialized (monitoring E key input)");
        _initialized = true;
    }

    internal static void Update()
    {
        if (!_initialized) return;

        try
        {
            // Detect when player presses E
            if (Input.GetKeyDown(KeyCode.E))
            {
                var target = FindClosestInteractable();
                if (target != null)
                {
                    var label = GetObjectLabel(target);
                    ScreenReader.Say(label, interrupt: true);
                    _lastAnnouncedObject = target.name;
                }
            }

            // Monitor proximity continuously
            var nearby = FindClosestInteractable();
            if (nearby != null)
            {
                if (nearby.name != _lastAnnouncedObject)
                {
                    var label = GetObjectLabel(nearby);
                    ScreenReader.Say(label, interrupt: false);
                    _lastAnnouncedObject = nearby.name;
                }
            }
            else if (_lastAnnouncedObject != null)
            {
                _lastAnnouncedObject = null;
            }

            // Ground drops (bodies/loot) only highlight visually and have no interaction
            // bubble, so a blind player gets no cue they can pick something up.
            AnnounceHighlightedDrop();

            // Knowing whether a body is in hand matters: doors like the mortuary gate on
            // HasOverheadBody(), so announce the carry state on change.
            AnnounceCarryState();

            // Station crafts (autopsy etc.) are timed and silent — tell the player to hold
            // still while one runs, and announce when it's done. Reuses the nearby reference.
            AnnounceCraftState(nearby);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[INTERACTION] Error: {ex.Message}");
        }
    }

    // Announce when a carryable ground drop becomes highlighted (i.e. close + faced),
    // which is exactly when vanilla E will pick it up.
    private static void AnnounceHighlightedDrop()
    {
        try
        {
            var drop = DropResGameObject.currently_higlighted_obj;
            if (drop == null || drop.is_collected ||
                drop.res == null || drop.res.IsEmpty() || drop.res.definition == null)
            {
                _lastHighlightedDropId = 0;
                return;
            }

            int id = drop.GetInstanceID();
            if (id == _lastHighlightedDropId) return;
            _lastHighlightedDropId = id;

            var name = ScreenReader.StripNguiCodes(drop.res.definition.GetItemName() ?? "").Trim();
            if (string.IsNullOrEmpty(name)) name = drop.res.id;
            ScreenReader.Say($"{name}, press E to pick up", interrupt: false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTERACTION] highlighted-drop announce failed: {ex.Message}");
        }
    }

    // Announce transitions of the player's overhead carry slot (e.g. picking up / putting
    // down a corpse), so the player knows whether they're carrying a body.
    private static void AnnounceCarryState()
    {
        try
        {
            var character = MainGame.me?.player?.components?.character;
            if (character == null) return;

            bool carrying = character.has_overhead;
            if (carrying == _wasCarrying) return;
            _wasCarrying = carrying;

            if (carrying)
            {
                string name = null;
                try { name = ScreenReader.StripNguiCodes(character.GetOverheadItem()?.definition?.GetItemName() ?? "").Trim(); }
                catch { }
                ScreenReader.Say(string.IsNullOrEmpty(name) ? "Carrying item" : $"Carrying {name}", interrupt: false);
            }
            else
            {
                // The body just left the overhead slot. If a nearby table/station now holds
                // it, tell the player it landed (and how to use it) instead of a bare "Hands
                // free" — the placement is otherwise silent (PutOverheadToWGO logs nothing).
                var table = FindNearbyObjectHoldingBody();
                if (table != null)
                {
                    var label = GetObjectLabel(table);
                    ScreenReader.Say($"Body placed on {label}, press E to open", interrupt: false);
                }
                else
                {
                    ScreenReader.Say("Hands free", interrupt: false);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTERACTION] carry-state announce failed: {ex.Message}");
        }
    }

    // Announce when an adjacent station starts/finishes a timed craft. Station crafts (cut
    // flesh on the autopsy table, etc.) set the station's craft component to is_crafting and
    // require the player to stand and perform the work; a blind player gets no cue, so they
    // move and cancel it — which on the autopsy table strands the body and wedges the station.
    private static void AnnounceCraftState(WorldGameObject station)
    {
        try
        {
            CraftComponent craft = null;
            // Only inspect the station the player is actually standing at (the nearest
            // interactable). Stations the player has used already have components inited, so
            // this is cheap and avoids a fresh scene scan.
            if (station != null && station.obj_def != null && station.obj_def.has_craft)
                craft = station.components?.craft;

            bool crafting = craft != null && craft.is_crafting;
            if (crafting == _wasCrafting) return;
            _wasCrafting = crafting;

            if (crafting)
            {
                string name = null;
                try
                {
                    var output = craft.current_craft?.GetFirstRealOutput();
                    name = ScreenReader.StripNguiCodes(output?.definition?.GetItemName() ?? "").Trim();
                }
                catch { }

                ScreenReader.Say(string.IsNullOrEmpty(name)
                    ? "Crafting, stand still until it finishes"
                    : $"Crafting {name}, stand still until it finishes", interrupt: false);
            }
            else
            {
                // Finished (or the craft was cleared). The inventory handler announces the
                // delivered item separately; this just signals the station is free again.
                ScreenReader.Say("Craft finished", interrupt: false);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTERACTION] craft-state announce failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Find the closest interactable object within reach whose inventory now contains a body
    /// (e.g. the autopsy table after laying a corpse down). Used to confirm placement audibly.
    /// </summary>
    private static WorldGameObject FindNearbyObjectHoldingBody()
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null) return null;
            var playerPos = player.pos;

            WorldGameObject best = null;
            float bestDist = float.MaxValue;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType<WorldGameObject>(true))
            {
                if (obj == null || IsPlayer(obj) || IsPrefab(obj)) continue;
                if (!obj.gameObject.activeInHierarchy) continue;

                // 1 tile = 96 world units; only consider objects within a couple of tiles.
                var dist = Vector2.Distance(obj.pos, playerPos);
                if (dist > 240f || dist >= bestDist) continue;

                Item body = null;
                try { body = obj.GetBodyFromInventory(); } catch { }
                if (body == null) continue;

                best = obj;
                bestDist = dist;
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static WorldGameObject FindClosestInteractable()
    {
        try
        {
            if (MainGame.me?.player == null)
                return null;

            var playerPos = MainGame.me.player.transform.position;
            var allObjects = UnityEngine.Object.FindObjectsOfType<WorldGameObject>(true);

            if (allObjects == null || allObjects.Length == 0)
                return null;

            // Find closest object, filtering out inactive ones
            var nearby = allObjects
                .Where(obj => obj != null && !IsPlayer(obj) && !IsPrefab(obj))
                .Where(obj => obj.gameObject.activeInHierarchy)
                .OrderBy(obj => Vector3.Distance(obj.transform.position, playerPos))
                .FirstOrDefault();

            if (nearby == null)
                return null;

            var distance = Vector3.Distance(nearby.transform.position, playerPos);
            if (distance > InteractionRange)
                return null;

            return nearby;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[INTERACTION] Error: {ex.Message}");
            return null;
        }
    }

    internal static bool IsPlayer(WorldGameObject obj)
    {
        return obj.name.Contains("Player");
    }

    internal static bool IsPrefab(WorldGameObject obj)
    {
        return obj.name.Contains("prefab") || obj.name.Contains("Prefab") || obj.name.Contains("template");
    }

    internal static string GetObjectLabel(WorldGameObject wgo)
    {
        if (wgo == null)
            return null;

        // Check if this is an exit/door (teleport object). Label it by destination so a
        // blind player can tell which door leads where (e.g. into the mortuary vs back out)
        // instead of hearing a row of identical "Door" entries.
        if (IsExitObject(wgo))
        {
            return GetDoorLabel(wgo);
        }

        // Try to get a label from the object definition
        try
        {
            if (wgo.obj_def != null)
            {
                // Try to use the object id, localized to a readable name where possible
                if (!string.IsNullOrEmpty(wgo.obj_def.id))
                    return LocalizedObjectName(wgo.obj_def.id);

                // Fall back to interaction type
                var typeString = wgo.obj_def.interaction_type.ToString();
                if (!string.IsNullOrEmpty(typeString))
                    return CleanObjectName(typeString);
            }
        }
        catch
        {
            // Fall back to object name if obj_def access fails
        }

        return CleanObjectName(wgo.name);
    }

    /// <summary>
    /// Name a teleport door by where it leads, derived from its obj_id
    /// (teleport_inside / teleport_outside / teleport_hatch / ...). Falls back to "Door".
    /// </summary>
    internal static string GetDoorLabel(WorldGameObject wgo)
    {
        var id = (wgo?.obj_id ?? "").ToLowerInvariant();
        if (id.Contains("inside")) return "Door inside";
        if (id.Contains("outside")) return "Door outside";
        if (id.Contains("hatch")) return "Hatch";
        if (id.Contains("stairs")) return "Stairs";
        if (id.Contains("dungeon")) return "Dungeon entrance";
        return "Door";
    }

    private static bool IsExitObject(WorldGameObject wgo)
    {
        if (wgo == null || wgo.obj_def == null)
            return false;

        // Check by object name pattern (teleport objects are exits)
        if (wgo.name.IndexOf("teleport", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        // Check by interaction type if available
        try
        {
            // If obj_def has interaction_type property, check it
            var interactionType = wgo.obj_def?.interaction_type;
            if (interactionType != null)
            {
                var typeString = interactionType.ToString();
                if (typeString.IndexOf("Teleport", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        catch
        {
            // If we can't access interaction_type, fall back to name-based detection
        }

        return false;
    }

    /// <summary>
    /// Localized, human-readable name for an object-definition id. The game stores object
    /// names under their id key (see WorldGameObject: <c>GJL.L(this.obj_def.id)</c>), so
    /// "mf_preparation_1" resolves to "Autopsy table" / "Obduktionstisch" instead of the
    /// raw "Mf preparation 1". Falls back to the prettified id when there is no translation.
    /// </summary>
    private static string LocalizedObjectName(string objId)
    {
        try
        {
            var loc = ScreenReader.StripNguiCodes(GJL.L(objId) ?? "").Trim();
            // GJL.L echoes the key back (or returns a "!key!" marker) when a translation
            // is missing — only use the result when it is a real, different string.
            if (!string.IsNullOrEmpty(loc) &&
                !loc.Equals(objId, StringComparison.OrdinalIgnoreCase) &&
                loc.IndexOf('!') < 0)
                return loc;
        }
        catch { }
        return CleanObjectName(objId);
    }

    private static string CleanObjectName(string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
            return objectName;

        // Remove [wgo] prefix
        var cleaned = objectName.StartsWith("[wgo]")
            ? objectName.Substring(5).Trim()
            : objectName;

        // Replace underscores and hyphens with spaces
        cleaned = cleaned.Replace("_", " ").Replace("-", " ");

        // Remove (Clone) suffix
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s*\(Clone\)\s*$", "");

        // Capitalize first letter
        if (cleaned.Length > 0)
            cleaned = char.ToUpper(cleaned[0]) + cleaned.Substring(1);

        return cleaned.Trim();
    }
}
