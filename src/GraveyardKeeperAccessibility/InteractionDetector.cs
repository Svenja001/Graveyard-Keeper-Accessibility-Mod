namespace GraveyardKeeperAccessibility;

internal static class InteractionDetector
{
    private static string _lastAnnouncedObject = null;
    private static int _lastHighlightedDropId = 0;
    private static bool _wasCarrying = false;
    private static bool _wasCrafting = false;
    private static WorldGameObject _lastWorkHighlight = null;
    private static bool _wasWorking = false;
    private static float _workAnnounceAccum = 0f;
    private static ManualLogSource _log;
    private static bool _initialized = false;
    private const float InteractionRange = 300f;

    /// <summary>
    /// True while a station next to the player is mid-craft (autopsy table cutting flesh, etc.).
    /// The navigator reads this to refuse auto-walk so the player doesn't accidentally abandon a
    /// half-finished craft. Updated each frame by <see cref="AnnounceWorkState"/>.
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
                // The game blocks interaction with most objects during the tutorial/intro
                // (WorldGameObject.CheckIfDisabledInTutorial). To a blind player that just
                // feels like "nothing happens", so when the object the game would interact
                // with is tutorial-locked, say why and point at the current task instead.
                var gameNearest = GetGameInteractionNearest();
                if (gameNearest != null && IsTutorialDisabled(gameNearest))
                {
                    var label = GetObjectLabel(gameNearest);
                    ScreenReader.Say($"{label}. Not available during the intro. Press R for your current task.", interrupt: true);
                    _lastAnnouncedObject = gameNearest.name;
                }
                else
                {
                    var target = FindClosestInteractable();
                    if (target != null)
                    {
                        var label = GetObjectLabel(target);
                        ScreenReader.Say(label, interrupt: true);
                        _lastAnnouncedObject = target.name;
                    }
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

            // Work actions (hold F to craft/dig/chop/...) are invisible to a blind player:
            // they get no "Press F" prompt and no progress cue. Announce both.
            AnnounceWorkState(nearby);
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

    // Narrate the "hold F to work" loop a sighted player relies on but a blind player can't
    // see: the on-screen "Press F" prompt, the work-in-progress animation, and craft completion.
    //
    // Work in GK (cut flesh on the autopsy table, dig a grave, chop a tree...) is performed by
    // HOLDING GameKey.Work (default F) while standing on the object's dock point. The game shows
    // a "Press F" bubble and sets character.wgo_hilighted_for_work when you're in position; while
    // you hold F the character plays the Tool animation (anim_state == Tool) and progress fills.
    private static void AnnounceWorkState(WorldGameObject nearby)
    {
        try
        {
            var character = MainGame.me?.player?.components?.character;
            if (character == null) return;

            // 1) "Press F to {verb}" — fires when a new object becomes highlighted for work,
            //    i.e. exactly when the game would show the on-screen F prompt.
            WorldGameObject highlight = null;
            try { highlight = character.wgo_hilighted_for_work; } catch { }
            if (highlight != _lastWorkHighlight)
            {
                _lastWorkHighlight = highlight;
                if (highlight != null)
                    ScreenReader.Say($"Press F to {GetWorkVerb(highlight)}", interrupt: false);
            }

            // 2) "In progress" — while the character is actually working (F held, Tool anim).
            bool working = false;
            try { working = character.anim_state == CharAnimState.Tool; } catch { }
            if (working)
            {
                if (!_wasWorking) _workAnnounceAccum = 0f;
                _workAnnounceAccum += Time.deltaTime;
                if (_workAnnounceAccum >= 2f)
                {
                    _workAnnounceAccum = 0f;
                    ScreenReader.Say("In progress", interrupt: false);
                }
            }
            _wasWorking = working;

            // 3) "Finished" — when a station craft (autopsy etc.) completes. Crafts clear
            //    is_crafting on completion; dig/chop completion announces itself naturally
            //    (the grave opens, drops appear), so we only add a cue for crafts.
            CraftComponent craft = null;
            if (nearby != null && nearby.obj_def != null && nearby.obj_def.has_craft)
                craft = nearby.components?.craft;
            bool crafting = craft != null && craft.is_crafting && craft.current_craft != null;
            if (crafting != _wasCrafting)
            {
                _wasCrafting = crafting;
                if (!crafting)
                    ScreenReader.Say("Finished", interrupt: false);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTERACTION] work-state announce failed: {ex.Message}");
        }
    }

    // The action verb a sighted player sees on the F prompt. Crafting stations say
    // "craft {output}"; tool actions map the required tool to its verb (shovel→dig, etc.).
    private static string GetWorkVerb(WorldGameObject wgo)
    {
        try
        {
            var craft = (wgo.obj_def != null && wgo.obj_def.has_craft) ? wgo.components?.craft : null;
            if (craft != null && craft.is_crafting && craft.current_craft != null)
            {
                string outName = null;
                try { outName = ScreenReader.StripNguiCodes(craft.current_craft.GetFirstRealOutput()?.definition?.GetItemName() ?? "").Trim(); }
                catch { }
                return string.IsNullOrEmpty(outName) ? "craft" : $"craft {outName}";
            }

            var actions = wgo.obj_def?.tool_actions;
            if (actions != null && !actions.no_actions && actions.action_tools != null && actions.action_tools.Count > 0)
            {
                switch (actions.action_tools[0])
                {
                    case ItemDefinition.ItemType.Shovel: return "dig";
                    case ItemDefinition.ItemType.Axe: return "chop";
                    case ItemDefinition.ItemType.Pickaxe: return "mine";
                    case ItemDefinition.ItemType.Hammer: return "build";
                    case ItemDefinition.ItemType.Hand: return "gather";
                }
            }

            if (craft != null) return "craft";
        }
        catch { }
        return "work";
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

    // The object the game considers "in reach" for an E press (its highlighted interaction
    // target), or null. This is what the game would actually act on, so it's the right thing
    // to test for the tutorial lock rather than our looser nearest-by-distance scan.
    private static WorldGameObject GetGameInteractionNearest()
    {
        try { return MainGame.me?.player?.components?.interaction?.nearest; }
        catch { return null; }
    }

    // True when the game would refuse to interact with this object because the player is still
    // in the tutorial and the object isn't flagged interactive_in_tutorial
    // (see WorldGameObject.CheckIfDisabledInTutorial / GameSave.IsInTutorial).
    private static bool IsTutorialDisabled(WorldGameObject wgo)
    {
        try
        {
            return wgo?.obj_def != null
                && !wgo.obj_def.interactive_in_tutorial
                && MainGame.me?.save != null
                && MainGame.me.save.IsInTutorial();
        }
        catch
        {
            return false;
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
    /// Name a teleport door so doors that share an obj_id are still distinguishable. The door
    /// "kind" comes from obj_id (teleport_inside / teleport_outside / hatch / stairs / dungeon);
    /// the destination comes from custom_tag, which the game formats as
    /// "tp_&lt;place&gt;_&lt;a|b&gt;[...]" (e.g. "tp_tavern_from_cellar_b_", "tp_mortuary_hatch_2_b").
    /// So a row of identical "Door inside" entries becomes "Door inside: Tavern cellar",
    /// "Door inside: Mortuary", etc. Falls back to the bare kind when no place can be recovered.
    /// </summary>
    internal static string GetDoorLabel(WorldGameObject wgo)
    {
        var id = (wgo?.obj_id ?? "").ToLowerInvariant();
        string kind =
            id.Contains("inside") ? "Door inside" :
            id.Contains("outside") ? "Door outside" :
            id.Contains("hatch") ? "Hatch" :
            id.Contains("stairs") ? "Stairs" :
            id.Contains("dungeon") ? "Dungeon entrance" :
            "Door";

        var place = DoorPlaceFromTag(wgo?.custom_tag);
        return string.IsNullOrEmpty(place) ? kind : $"{kind}: {place}";
    }

    /// <summary>
    /// Recover a human-readable destination from a teleport door's custom_tag. Tags follow the
    /// game's "tp_&lt;place&gt;_&lt;a|b&gt;[_extra][_]" convention (see the teleport spawns in
    /// GameSave and Flow_TeleportToWGO, which itself splits the tag on '_' and treats index 1 as
    /// the place key). Pair-end markers (a/b), direction connectors, numeric suffixes and the kind
    /// words already conveyed by obj_id are stripped, leaving the descriptive place words.
    /// Returns null when nothing meaningful remains.
    /// </summary>
    internal static string DoorPlaceFromTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        tag = tag.ToLowerInvariant().Trim();
        if (!tag.StartsWith("tp_")) return null;

        // Drop the "tp_" prefix and the trailing "_" that marks the door object (vs its anchor).
        var body = tag.Substring(3).Trim('_');

        var words = new List<string>();
        foreach (var part in body.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "a":
                case "b":
                case "to":
                case "from":
                case "hatch":
                case "stairs":
                case "inside":
                case "outside":
                    continue;
            }
            if (int.TryParse(part, out _)) continue;
            words.Add(part);
        }

        if (words.Count == 0) return null;

        var place = string.Join(" ", words).Replace("-", " ").Trim();
        // Guard against unhelpful single-letter tokens (e.g. "tp_h_a_").
        if (place.Length < 2) return null;
        return char.ToUpper(place[0]) + place.Substring(1);
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
    internal static string LocalizedObjectName(string objId)
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
