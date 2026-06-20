namespace GraveyardKeeperAccessibility;

internal static class TitleScreenAccessibility
{
    internal static TitleScreen _currentScreen;
    internal static readonly List<GUIElement> Elements = new();
    internal static int SelectedIndex = -1;
    private static bool _announced;

    // Set true when the player loads a save or starts a new game from the save-slots screen. The
    // title screen flashes back up during the load transition, which would otherwise re-announce
    // "Title Screen"; instead we say "Loading" once and clear the flag.
    internal static bool LoadingStarted;

    internal static bool HasActiveScreen => _currentScreen != null;

    internal static void OnScreenOpened(TitleScreen screen)
    {
        if (screen == _currentScreen) return;
        if (screen == null) return;

        _currentScreen = screen;
        _announced = false;
        ScreenReader.ClearMenuContext();
        Elements.Clear();
        SelectedIndex = -1;

        // The player just chose a save slot / new game: this is the load transition, not the menu
        // coming back. Announce "Loading" rather than re-reading the title screen.
        if (LoadingStarted)
        {
            LoadingStarted = false;
            _announced = true;
            ScreenReader.Say("Loading");
            return;
        }

        DiscoverElements(screen);

        var active = GetActiveElements();
        Plugin.Log.LogInfo($"Title screen opened, {active.Count} elements discovered");

        if (active.Count > 0)
        {
            // Auto-focus the first entry so the player doesn't have to press down once first.
            _announced = true;
            SelectedIndex = 0;
            ScreenReader.Say($"Title Screen. {active[0].ReadLabel()}");
        }
        else if (!_announced)
        {
            _announced = true;
            ScreenReader.Say("Title Screen");
        }
    }

    internal static void OnScreenClosed(TitleScreen screen)
    {
        if (screen != _currentScreen) return;

        Plugin.Log.LogInfo("Title screen closed, switching to GUI accessibility");
        _currentScreen = null;
        _announced = false;
        ScreenReader.ClearMenuContext();
        Elements.Clear();
        SelectedIndex = -1;
    }

    private static void DiscoverElements(TitleScreen screen)
    {
        if (screen == null) return;

        try
        {
            var buttons = screen.GetComponentsInChildren<UIButton>(true);
            Plugin.Log.LogInfo($"[TitleScreen] Found {buttons?.Length ?? 0} UIButton components");
            if (buttons == null || buttons.Length == 0) return;

            foreach (var button in buttons)
            {
                if (button == null) continue;
                var ownLabel = button.GetComponentInChildren<UILabel>();

                string text = null;
                if (ownLabel != null)
                {
                    text = ScreenReader.StripNguiCodes(ownLabel.text);
                }

                // Fallback: use button name if no UILabel found or label is empty
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = button.name;
                    if (string.IsNullOrWhiteSpace(text) || text.Length <= 1)
                    {
                        Plugin.Log.LogInfo($"[TitleScreen] Skipping button '{button.name}' - no valid label");
                        continue;
                    }
                }

                Plugin.Log.LogInfo($"[TitleScreen] Adding button: '{text}' (name: {button.name})");
                Elements.Add(new GUIElement
                {
                    Go = button.gameObject,
                    Label = text,
                    Type = ElementType.Button
                });
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Error discovering title screen elements: {ex.Message}");
        }
    }

    internal static List<GUIElement> GetActiveElements()
    {
        return Elements.Where(e => e.Go != null && e.Go.activeInHierarchy).ToList();
    }

    internal static void SelectIndex(int index)
    {
        var active = GetActiveElements();
        if (active.Count == 0) return;

        SelectedIndex = index;
        var elem = active[SelectedIndex];
        ScreenReader.Say(elem.ReadLabel());
    }

    internal static void ActivateSelected()
    {
        var active = GetActiveElements();
        if (SelectedIndex < 0 || SelectedIndex >= active.Count) return;

        var elem = active[SelectedIndex];
        if (elem.Type == ElementType.Button)
        {
            var button = elem.Go.GetComponent<UIButton>();
            if (button == null) return;
            button.SetState(UIButtonColor.State.Pressed, false);
            elem.Go.SendMessage("OnPress", true, SendMessageOptions.DontRequireReceiver);
            elem.Go.SendMessage("OnPress", false, SendMessageOptions.DontRequireReceiver);
            elem.Go.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
            button.SetState(UIButtonColor.State.Normal, false);
        }
    }

    internal static void CheckForTitleScreen()
    {
        try
        {
            // If a BaseGUI is visible (like MainMenuGUI), close title screen immediately
            // This handles the case where TitleScreen component is shown as part of MainMenuGUI
            var hasBaseGUI = GUIAccessibility.HasActiveGUI;
            if (hasBaseGUI && _currentScreen != null)
            {
                OnScreenClosed(_currentScreen);
                return;
            }

            var screen = UnityEngine.Object.FindObjectOfType<TitleScreen>();

            // Check both that it exists and that it's actually visible (using is_shown property)
            bool isVisible = false;
            if (screen != null && screen.gameObject.activeInHierarchy)
            {
                try
                {
                    // TitleScreen likely has is_shown property like BaseGUI
                    var isShownProp = screen.GetType().GetProperty("is_shown");
                    if (isShownProp != null)
                    {
                        isVisible = (bool)isShownProp.GetValue(screen);
                        if (_currentScreen != null && !isVisible)
                            Plugin.Log.LogInfo($"[TitleScreen] is_shown = {isVisible}, closing screen");
                    }
                    else
                    {
                        // No is_shown property - check if canvas is enabled instead
                        var canvas = screen.GetComponent<CanvasGroup>();
                        if (canvas != null)
                            isVisible = canvas.alpha > 0;
                        else
                            isVisible = true; // Fallback if no visibility tracking found
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Error checking title screen visibility: {ex.Message}");
                    // If property access fails, assume it's visible if gameObject is active
                    isVisible = true;
                }
            }

            if (isVisible)
            {
                if (_currentScreen != screen)
                    OnScreenOpened(screen);
            }
            else
            {
                if (_currentScreen != null)
                    OnScreenClosed(_currentScreen);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Error in CheckForTitleScreen: {ex.Message}");
        }
    }
}
