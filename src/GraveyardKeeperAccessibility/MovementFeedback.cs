namespace GraveyardKeeperAccessibility;

internal static class MovementFeedback
{
    private static Vector3 _lastPosition = Vector3.zero;
    private static float _stepDistance = 0.5f; // Distance per "step" in meters
    private static float _distanceSinceLastClick = 0f;
    private static ManualLogSource _log;
    private static bool _initialized = false;
    private static bool _playerNotFoundLogged = false;
    private const string CLICK_SOUND = "•"; // Using non-breaking character for click sound via TTS

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _initialized = true;
        _log?.LogInfo("[MOVEMENT] Movement feedback initialized (using TTS clicks)");
    }

    internal static void Update()
    {
        if (!_initialized) return;

        try
        {
            var player = FindPlayer();
            if (player == null)
            {
                if (!_playerNotFoundLogged)
                {
                    _log?.LogWarning("[MOVEMENT] Player not found yet (will retry)");
                    _playerNotFoundLogged = true;
                }
                return;
            }

            _playerNotFoundLogged = false;

            var currentPos = player.transform.position;

            if (_lastPosition == Vector3.zero)
            {
                _lastPosition = currentPos;
                return;
            }

            float distanceMoved = Vector3.Distance(currentPos, _lastPosition);
            _distanceSinceLastClick += distanceMoved;

            if (_distanceSinceLastClick >= _stepDistance)
            {
                PlayClick();
                _distanceSinceLastClick -= _stepDistance;
            }

            _lastPosition = currentPos;
        }
        catch (Exception ex)
        {
            _log?.LogError($"MovementFeedback error: {ex.Message}");
        }
    }

    private static GameObject FindPlayer()
    {
        var playerNames = new[] { "Player(Clone)", "Player", "player", "MainCharacter", "Necromancer", "PlayerCharacter" };
        foreach (var name in playerNames)
        {
            var player = GameObject.Find(name);
            if (player != null)
            {
                _log?.LogInfo($"[MOVEMENT] Found player: {name}");
                return player;
            }
        }

        var playerByTag = GameObject.FindGameObjectWithTag("Player");
        if (playerByTag != null)
        {
            _log?.LogInfo($"[MOVEMENT] Found player by tag: {playerByTag.name}");
            return playerByTag;
        }

        // Debug: log some GameObjects that exist
        if (!_playerNotFoundLogged)
        {
            try
            {
                var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
                var relevantObjects = System.Linq.Enumerable.Where(allObjects,
                    go => go.name.Contains("Player") || go.name.Contains("player") || go.CompareTag("Player"))
                    .Take(5);
                var objList = string.Join(", ", relevantObjects.Select(o => o.name));
                _log?.LogInfo($"[MOVEMENT] Game objects with 'Player': {(string.IsNullOrEmpty(objList) ? "none found" : objList)}");
            }
            catch { }
        }

        return null;
    }

    private static void PlayClick()
    {
        try
        {
            // Use TTS to play a click sound - this is more reliable than audio generation
            ScreenReader.Say(CLICK_SOUND, interrupt: false);
            _log?.LogInfo("[MOVEMENT] Click played");
        }
        catch (Exception ex)
        {
            _log?.LogError($"PlayClick error: {ex.Message}");
        }
    }
}
