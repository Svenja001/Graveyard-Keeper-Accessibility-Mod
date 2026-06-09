namespace GraveyardKeeperAccessibility;

internal static class ClickHandler
{
    private static ManualLogSource _log;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _log?.LogInfo($"[CLICK] ClickHandler initialized - LeftClick=Ctrl+Enter, RightClick=Shift+Enter");
    }

    internal static void Update()
    {
        // Ctrl+Enter for left-click
        if (Input.GetKeyDown(KeyCode.Return) && Input.GetKey(KeyCode.LeftControl))
        {
            HandleLeftClick();
        }
        // Shift+Enter for right-click
        else if (Input.GetKeyDown(KeyCode.Return) && Input.GetKey(KeyCode.LeftShift))
        {
            HandleRightClick();
        }
    }

    private static void HandleLeftClick()
    {
        try
        {
            if (GUIAccessibility.HasActiveGUI)
            {
                HandleGUIClick(isRightClick: false);
            }
            else
            {
                HandleWorldClick(isRightClick: false);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[CLICK] Error handling left-click: {ex.Message}");
        }
    }

    private static void HandleRightClick()
    {
        try
        {
            if (GUIAccessibility.HasActiveGUI)
            {
                HandleGUIClick(isRightClick: true);
            }
            else
            {
                HandleWorldClick(isRightClick: true);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[CLICK] Error handling right-click: {ex.Message}");
        }
    }

    private static void HandleGUIClick(bool isRightClick)
    {
        var active = GUIAccessibility.GetActiveElements();
        if (GUIAccessibility.SelectedIndex < 0 || GUIAccessibility.SelectedIndex >= active.Count)
            return;

        var elem = active[GUIAccessibility.SelectedIndex];
        var clickType = isRightClick ? "right-click" : "left-click";

        _log?.LogInfo($"[CLICK] {clickType} on {elem.Label}");

        // Send click message to the element
        var go = elem.Go;
        if (go == null) return;

        // Try to find a UIButton and trigger it
        var button = go.GetComponent<UIButton>();
        if (button == null)
            button = go.GetComponentInChildren<UIButton>();
        if (button == null)
            button = go.GetComponentInParent<UIButton>();

        if (button != null)
        {
            button.SetState(UIButtonColor.State.Pressed, false);
            button.gameObject.SendMessage("OnPress", true, SendMessageOptions.DontRequireReceiver);
            button.gameObject.SendMessage("OnPress", false, SendMessageOptions.DontRequireReceiver);

            // Dispatch left or right click based on the key pressed
            if (isRightClick)
            {
                button.gameObject.SendMessage("OnRightClick", SendMessageOptions.DontRequireReceiver);
            }
            else
            {
                button.gameObject.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
            }

            button.SetState(UIButtonColor.State.Normal, false);
        }
        else
        {
            // Send messages directly to the element
            if (isRightClick)
            {
                go.SendMessage("OnRightClick", SendMessageOptions.DontRequireReceiver);
            }
            else
            {
                go.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
            }
        }
    }

    private static void HandleWorldClick(bool isRightClick)
    {
        // For world interactions, we'd need to find the closest interactable object
        // and trigger either OnClick (left) or OnRightClick (right)
        // This is less common but could be useful for context menus

        try
        {
            if (MainGame.me?.player == null) return;

            var playerPos = MainGame.me.player.transform.position;
            var allObjects = UnityEngine.Object.FindObjectsOfType<WorldGameObject>(true);

            if (allObjects == null || allObjects.Length == 0) return;

            // Find closest interactable within range
            var nearby = allObjects
                .Where(obj => obj != null && !InteractionDetector.IsPlayer(obj) && !InteractionDetector.IsPrefab(obj))
                .Where(obj => obj.gameObject.activeInHierarchy)
                .OrderBy(obj => Vector3.Distance(obj.transform.position, playerPos))
                .FirstOrDefault();

            if (nearby == null) return;

            var distance = Vector3.Distance(nearby.transform.position, playerPos);
            if (distance > 300f) return;

            var clickType = isRightClick ? "right-click" : "left-click";
            _log?.LogInfo($"[CLICK] {clickType} on world object: {nearby.name}");

            var go = nearby.gameObject;
            if (isRightClick)
            {
                go.SendMessage("OnRightClick", SendMessageOptions.DontRequireReceiver);
            }
            else
            {
                go.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[CLICK] Error handling world click: {ex.Message}");
        }
    }
}
