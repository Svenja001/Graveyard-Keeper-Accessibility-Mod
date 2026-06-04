namespace GraveyardKeeperAccessibility;

internal enum ElementType { Button, Switcher, Slider }

internal class GUIElement
{
    internal GameObject Go;
    internal string Label;
    internal ElementType Type;
    internal UIButton DecButton;
    internal UIButton IncButton;
    internal UISlider Slider;
    internal UILabel ValueLabel;

    internal string ReadLabel()
    {
        if (ValueLabel != null && ValueLabel.gameObject.activeInHierarchy)
        {
            var val = ScreenReader.StripNguiCodes(ValueLabel.text);
            if (!string.IsNullOrWhiteSpace(val))
                return Label + ": " + val;
        }

        if (Slider != null)
            return Label + ": " + Mathf.RoundToInt(Slider.value * 100);

        return Label;
    }
}

internal static class GUIAccessibility
{
    private static BaseGUI _currentGUI;
    internal static readonly List<GUIElement> Elements = new();
    internal static int SelectedIndex = -1;

    internal static bool HasActiveGUI => _currentGUI != null;

    internal static void OnGUIOpened(BaseGUI gui)
    {
        if (gui == _currentGUI) return;

        _currentGUI = gui;
        ScreenReader.ClearMenuContext();
        Elements.Clear();
        SelectedIndex = -1;

        DiscoverElements(gui);

        var guiName = gui.GetType().Name.Replace("GUI", "").Replace("Gui", "");
        var activeCount = Elements.Count(e => e.Go.activeInHierarchy);
        Plugin.Log.LogInfo($"GUI opened: {guiName}, {activeCount} elements");

        ScreenReader.Say(guiName);
    }

    internal static void OnGUIClosed(BaseGUI gui)
    {
        if (gui != _currentGUI) return;

        _currentGUI = null;
        ScreenReader.ClearMenuContext();
        Elements.Clear();
        SelectedIndex = -1;
    }

    private static void DiscoverElements(BaseGUI gui)
    {
        foreach (var button in gui.GetComponentsInChildren<UIButton>(true))
        {
            var slider = button.transform.parent?.GetComponent<UISlider>();
            if (slider != null) continue;

            var switcher = button.transform.parent;
            if (switcher != null && (button.name == "dec" || button.name == "inc"))
            {
                var row = switcher.parent;
                if (row != null && !Elements.Any(e => e.Go == row.gameObject))
                {
                    var rowLabel = row.Find("label")?.GetComponent<UILabel>();
                    if (rowLabel == null) continue;

                    var decBtn = switcher.Find("dec")?.GetComponent<UIButton>();
                    var incBtn = switcher.Find("inc")?.GetComponent<UIButton>();
                    var valLabel = switcher.Find("label")?.GetComponent<UILabel>();

                    Elements.Add(new GUIElement
                    {
                        Go = row.gameObject,
                        Label = ScreenReader.StripNguiCodes(rowLabel.text),
                        Type = ElementType.Switcher,
                        DecButton = decBtn,
                        IncButton = incBtn,
                        ValueLabel = valLabel
                    });
                }
                continue;
            }

            var ownLabel = button.GetComponentInChildren<UILabel>();
            if (ownLabel == null) continue;
            var text = ScreenReader.StripNguiCodes(ownLabel.text);
            if (string.IsNullOrWhiteSpace(text) || text.Length <= 1) continue;

            Elements.Add(new GUIElement
            {
                Go = button.gameObject,
                Label = text,
                Type = ElementType.Button
            });
        }

        foreach (var slider in gui.GetComponentsInChildren<UISlider>(true))
        {
            var row = slider.transform.parent;
            if (row == null) continue;
            if (Elements.Any(e => e.Go == row.gameObject)) continue;

            var rowLabel = row.Find("label")?.GetComponent<UILabel>();
            if (rowLabel == null) continue;

            var counter = slider.transform.Find("counter")?.GetComponent<UILabel>();

            Elements.Add(new GUIElement
            {
                Go = row.gameObject,
                Label = ScreenReader.StripNguiCodes(rowLabel.text),
                Type = ElementType.Slider,
                Slider = slider,
                ValueLabel = counter
            });
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

    internal static void AdjustLeft()
    {
        var active = GetActiveElements();
        if (SelectedIndex < 0 || SelectedIndex >= active.Count) return;

        var elem = active[SelectedIndex];
        if (elem.Type == ElementType.Switcher && elem.DecButton != null)
        {
            var go = elem.DecButton.gameObject;
            go.SendMessage("OnPress", true, SendMessageOptions.DontRequireReceiver);
            go.SendMessage("OnPress", false, SendMessageOptions.DontRequireReceiver);
            go.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
            ScreenReader.Say(elem.ReadLabel());
        }
        else if (elem.Type == ElementType.Slider && elem.Slider != null)
        {
            AdjustSlider(elem, -0.05f);
        }
    }

    internal static void AdjustRight()
    {
        var active = GetActiveElements();
        if (SelectedIndex < 0 || SelectedIndex >= active.Count) return;

        var elem = active[SelectedIndex];
        if (elem.Type == ElementType.Switcher && elem.IncButton != null)
        {
            var go = elem.IncButton.gameObject;
            go.SendMessage("OnPress", true, SendMessageOptions.DontRequireReceiver);
            go.SendMessage("OnPress", false, SendMessageOptions.DontRequireReceiver);
            go.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
            ScreenReader.Say(elem.ReadLabel());
        }
        else if (elem.Type == ElementType.Slider && elem.Slider != null)
        {
            AdjustSlider(elem, 0.05f);
        }
    }

    private static void AdjustSlider(GUIElement elem, float delta)
    {
        // TODO: SmartSlider value changes don't persist — needs investigation
        ScreenReader.Say(elem.ReadLabel());
    }

    public static void OnHover(UIButtonColor instance, bool isOver)
    {
        if (_currentGUI == null) return;

        if (!isOver)
        {
            ScreenReader.ClearMenuContext();
            return;
        }

        var go = instance.gameObject;
        for (int i = 0; i < Elements.Count; i++)
        {
            var elem = Elements[i];
            if (elem.Go == go || go.transform.IsChildOf(elem.Go.transform))
            {
                var active = GetActiveElements();
                var activeIdx = active.IndexOf(elem);
                if (activeIdx >= 0)
                {
                    SelectedIndex = activeIdx;
                    ScreenReader.SayMenu(elem.ReadLabel());
                }
                return;
            }
        }
    }

    internal static void CheckForNewGUI()
    {
        if (!GUIElements.me) return;

        BaseGUI topGUI = null;
        foreach (var gui in GUIElements.me.GetComponentsInChildren<BaseGUI>(true))
        {
            if (!gui.is_shown) continue;
            if (gui is HUD) continue;
            topGUI = gui;
        }

        if (topGUI == _currentGUI) return;

        if (_currentGUI != null)
            OnGUIClosed(_currentGUI);

        if (topGUI != null)
            OnGUIOpened(topGUI);
    }
}
