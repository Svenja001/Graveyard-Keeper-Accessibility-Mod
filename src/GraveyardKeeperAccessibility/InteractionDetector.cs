namespace GraveyardKeeperAccessibility;

internal static class InteractionDetector
{
    private static string _lastAnnouncedObject = null;
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
        }
        catch (Exception ex)
        {
            _log?.LogError($"[INTERACTION] Error: {ex.Message}");
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

            // Find closest non-player, non-prefab object
            var nearby = allObjects
                .Where(obj => obj != null && !IsPlayer(obj) && !IsPrefab(obj))
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

    private static bool IsPlayer(WorldGameObject obj)
    {
        return obj.name.Contains("Player");
    }

    private static bool IsPrefab(WorldGameObject obj)
    {
        return obj.name.Contains("prefab") || obj.name.Contains("Prefab") || obj.name.Contains("template");
    }


    private static string GetObjectLabel(WorldGameObject wgo)
    {
        if (wgo == null)
            return null;

        // Check if this is an exit/door (teleport object)
        if (IsExitObject(wgo))
        {
            return "Door";
        }

        return CleanObjectName(wgo.name);
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
