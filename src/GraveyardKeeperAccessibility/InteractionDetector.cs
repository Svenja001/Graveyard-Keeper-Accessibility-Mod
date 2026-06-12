namespace GraveyardKeeperAccessibility;

internal static class InteractionDetector
{
    private static string _lastAnnouncedObject = null;
    private static int _lastHighlightedDropId = 0;
    private static bool _wasCarrying = false;
    private static ManualLogSource _log;
    private static bool _initialized = false;
    private const float InteractionRange = 300f;

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
                ScreenReader.Say("Hands free", interrupt: false);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTERACTION] carry-state announce failed: {ex.Message}");
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
                // Try to use the object id
                if (!string.IsNullOrEmpty(wgo.obj_def.id))
                    return CleanObjectName(wgo.obj_def.id);

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
