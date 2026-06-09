namespace GraveyardKeeperAccessibility;

internal struct NavigationTarget
{
    internal WorldGameObject Object;
    internal string Label;
    internal float Distance;
    internal Vector3 Position;
}

internal static class ObjectNavigator
{
    private static ManualLogSource _log;
    private static bool _initialized = false;
    private static int _selectedIndex = 0;
    private static List<NavigationTarget> _destinations = new();
    private static NavigationTarget _currentTarget;
    private static bool _isWalking = false;
    private static int _updateCounter = 0;
    private const float MaxNavDistance = 10000f;  // Game coordinates are in large units
    private const float ArrivalThreshold = 100f;  // Arrival threshold adjusted for large coordinate scale
    private const int UpdateInterval = 30; // Update object list every 30 frames

    internal static bool IsWalking => _isWalking;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _initialized = true;
        _log?.LogInfo("[NAVIGATOR] ObjectNavigator initialized (persistent mode)");
    }

    internal static void Update()
    {
        if (!_initialized) return;

        try
        {
            // Only update object list every UpdateInterval frames for performance
            _updateCounter++;
            if (_updateCounter >= UpdateInterval)
            {
                _updateCounter = 0;
                RefreshDestinations();
            }

            // Continue walking if active
            if (_isWalking)
            {
                ContinueWalking();
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error in Update: {ex.Message}");
        }
    }

    private static void RefreshDestinations()
    {
        try
        {
            var newDestinations = FindNearbyObjects();

            // If list changed significantly, reset selection to nearest
            if (_destinations.Count != newDestinations.Count)
            {
                _destinations = newDestinations;
                _selectedIndex = 0;
            }
            else
            {
                // Update distances but keep selection
                _destinations = newDestinations;
                if (_selectedIndex >= _destinations.Count)
                    _selectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error refreshing destinations: {ex.Message}");
        }
    }

    internal static void SelectNext()
    {
        if (_destinations.Count == 0) return;

        _selectedIndex = (_selectedIndex + 1) % _destinations.Count;
        AnnounceSelected();
    }

    internal static void SelectPrevious()
    {
        if (_destinations.Count == 0) return;

        _selectedIndex = (_selectedIndex - 1 + _destinations.Count) % _destinations.Count;
        AnnounceSelected();
    }

    internal static void AnnounceSelected()
    {
        if (_destinations.Count == 0)
        {
            ScreenReader.Say("No navigable objects nearby", interrupt: false);
            return;
        }

        try
        {
            var target = _destinations[_selectedIndex];
            var message = $"{target.Label}, {target.Distance:F0} meters away";
            ScreenReader.Say(message, interrupt: false);
            _log?.LogInfo($"[NAVIGATOR] Announced: {message}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error announcing: {ex.Message}");
        }
    }

    internal static void WalkToSelected()
    {
        if (_destinations.Count == 0) return;

        try
        {
            var player = MainGame.me?.player;
            if (player?.components?.character != null)
            {
                player.components.character.player_controlled_by_script = true;
            }

            _currentTarget = _destinations[_selectedIndex];
            _isWalking = true;

            ScreenReader.Say($"Walking to {_currentTarget.Label}", interrupt: true);
            _log?.LogInfo($"[NAVIGATOR] Starting walk to {_currentTarget.Label} at distance {_currentTarget.Distance:F1}m");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error starting walk: {ex.Message}");
        }
    }

    internal static void StopWalking()
    {
        if (!_isWalking) return;

        try
        {
            var player = MainGame.me?.player;
            if (player?.components?.character != null)
            {
                player.components.character.player_controlled_by_script = false;
            }

            _isWalking = false;
            ScreenReader.Say("Walking stopped", interrupt: true);
            _log?.LogInfo("[NAVIGATOR] Walking stopped");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error stopping walk: {ex.Message}");
        }
    }

    private static void ContinueWalking()
    {
        if (!_isWalking) return;

        try
        {
            var player = MainGame.me?.player;
            if (player == null)
            {
                _log?.LogError("[NAVIGATOR] Player is null during walk");
                StopWalking();
                return;
            }

            var currentPos = player.transform.position;
            var targetPos = _currentTarget.Position;
            var distance = Vector3.Distance(currentPos, targetPos);

            _log?.LogInfo($"[NAVIGATOR] Walking: current={currentPos}, target={targetPos}, distance={distance:F1}");

            // Check if arrived
            if (distance <= ArrivalThreshold)
            {
                var player2 = MainGame.me?.player;
                if (player2?.components?.character != null)
                {
                    player2.components.character.player_controlled_by_script = false;
                }

                _isWalking = false;
                ScreenReader.Say($"Arrived at {_currentTarget.Label}", interrupt: true);
                _log?.LogInfo("[NAVIGATOR] Arrived at destination");
                return;
            }

            // Move toward target at fast walking speed
            var direction = (targetPos - currentPos).normalized;
            var moveDistance = 20.0f; // Fast walking (20 units per frame ~1200 units/sec at 60fps)
            var newPos = currentPos + direction * moveDistance;

            _log?.LogInfo($"[NAVIGATOR] Moving from {currentPos} to {newPos} (moveDistance={moveDistance})");
            player.transform.position = newPos;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error during walk: {ex.Message}\n{ex.StackTrace}");
            StopWalking();
        }
    }

    private static string GetObjectLabelSafe(WorldGameObject obj)
    {
        try
        {
            // Special handling for graves by checking obj_id
            if (obj != null && !string.IsNullOrEmpty(obj.obj_id))
            {
                if (obj.obj_id.IndexOf("grave", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var cleanId = obj.obj_id.Replace("_", " ").Replace("-", " ");
                    if (cleanId.Length > 0)
                        cleanId = char.ToUpper(cleanId[0]) + cleanId.Substring(1);
                    return "Grave " + cleanId.Trim();
                }
            }

            return InteractionDetector.GetObjectLabel(obj);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] Failed to get label for object {obj.name}: {ex.Message}");
            return "Unknown Object";
        }
    }

    private static List<NavigationTarget> FindNearbyObjects()
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null)
            {
                _log?.LogWarning("[NAVIGATOR] Player is null");
                return new List<NavigationTarget>();
            }

            var playerPos = player.transform.position;
            var playerGridPos = player.grid_pos;
            var allObjects = UnityEngine.Object.FindObjectsOfType<WorldGameObject>(true);

            _log?.LogInfo($"[NAVIGATOR] Found {(allObjects?.Length ?? 0)} total WorldGameObjects");

            if (allObjects == null || allObjects.Length == 0)
            {
                _log?.LogWarning("[NAVIGATOR] No WorldGameObjects found");
                return new List<NavigationTarget>();
            }

            var filtered1 = allObjects
                .Where(obj => obj != null && !InteractionDetector.IsPlayer(obj) && !InteractionDetector.IsPrefab(obj))
                .ToList();
            _log?.LogInfo($"[NAVIGATOR] After null/player/prefab filter: {filtered1.Count} objects");

            var filtered2 = filtered1
                .Where(obj => obj.gameObject.activeInHierarchy)
                .ToList();
            _log?.LogInfo($"[NAVIGATOR] After active filter: {filtered2.Count} objects");

            var targets = filtered2
                .Select(obj => {
                    // Use transform.position for all objects, but keep walking horizontal (ignore Y differences)
                    var objPos = new Vector3(obj.transform.position.x, playerPos.y, obj.transform.position.z);
                    var distance = Vector3.Distance(objPos, playerPos);
                    var label = GetObjectLabelSafe(obj);

                    _log?.LogInfo($"[NAVIGATOR] Object: {obj.name} -> {label}, distance: {distance:F1}m");

                    return new NavigationTarget
                    {
                        Object = obj,
                        Label = label,
                        Position = objPos,
                        Distance = distance
                    };
                })
                .Where(t => t.Distance <= MaxNavDistance)
                .OrderBy(t => t.Distance)
                .ToList();

            _log?.LogInfo($"[NAVIGATOR] Final list: {targets.Count} navigable objects");
            return targets;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error finding objects: {ex.Message}");
            return new List<NavigationTarget>();
        }
    }
}
