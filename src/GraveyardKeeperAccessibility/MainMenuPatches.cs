namespace GraveyardKeeperAccessibility;

internal enum ElementType { Button, Switcher, Slider, ItemCell }

internal class GUIElement
{
    internal GameObject Go;
    internal string Label;
    internal ElementType Type;
    internal UIButton DecButton;
    internal UIButton IncButton;
    internal UISlider Slider;
    internal UILabel ValueLabel;
    internal BaseItemCellGUI Cell;

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
        Plugin.Log.LogInfo($"[GUI OPENED] {guiName}, {activeCount} elements");

        // Announce inventory items if this is an inventory/chest GUI
        InventoryItemHandler.OnGUIOpened(gui);

        // Log all UI text for debugging
        var allLabels = gui.GetComponentsInChildren<UILabel>(true);
        var textContent = string.Join(" | ", allLabels.Where(l => !string.IsNullOrWhiteSpace(l.text))
            .Select(l => ScreenReader.StripNguiCodes(l.text).Trim())
            .Take(10));
        if (!string.IsNullOrEmpty(textContent))
            Plugin.Log.LogInfo($"[GUI TEXT] {textContent}");

        // Special handling for dialogue GUIs
        if (guiName.Contains("Dialog") || guiName.Contains("Subtitle") || guiName.Contains("Caption"))
        {
            var dialogLabels = allLabels.Where(l => l.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(l.text))
                .Select(l => ScreenReader.StripNguiCodes(l.text).Trim())
                .Where(t => !string.IsNullOrEmpty(t) && t.Length > 1)
                .ToList();

            // Skip header/buttons and get the main dialogue content
            var mainDialogue = dialogLabels.FirstOrDefault(t => t.Length > 10 && !t.StartsWith("X") && !t.Equals("Ja", StringComparison.OrdinalIgnoreCase) && !t.Equals("Nein", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(mainDialogue))
            {
                Plugin.Log.LogInfo($"[DIALOGUE CONTENT] {mainDialogue}");
                ScreenReader.Say(mainDialogue);
                return;
            }
        }

        // If this GUI exposes navigable item cells (e.g. the autopsy table's body parts),
        // mention the count so the player knows there's a grid to arrow through. The cells'
        // names are read individually as the player navigates.
        var cellCount = GetActiveElements().Count(e => e.Type == ElementType.ItemCell);
        ScreenReader.Say(cellCount > 0 ? $"{guiName}, {cellCount} items" : guiName);
    }

    internal static void OnGUIClosed(BaseGUI gui)
    {
        if (gui != _currentGUI) return;

        _currentGUI = null;
        ScreenReader.ClearMenuContext();
        Elements.Clear();
        SelectedIndex = -1;

        InventoryItemHandler.OnGUIClosed(gui);
    }

    private static void DiscoverElements(BaseGUI gui)
    {
        // Dump entire hierarchy for debugging
        if (gui.GetType().Name == "SaveSlotsMenuGUI")
        {
            Plugin.Log.LogInfo($"[DiscoverElements] ===== SaveSlotsMenuGUI Hierarchy =====");
            DumpHierarchy(gui.gameObject, 0);
            Plugin.Log.LogInfo($"[DiscoverElements] ===== End Hierarchy =====");
        }

        var buttons = gui.GetComponentsInChildren<UIButton>(true);
        Plugin.Log.LogInfo($"[DiscoverElements] Found {buttons.Length} UIButton components in {gui.GetType().Name}");
        Plugin.Log.LogInfo($"[DiscoverElements] Button names: {string.Join(", ", buttons.Select(b => b.name))}");

        foreach (var button in buttons)
        {
            // Skip banner and promotional buttons
            if (button.name.Contains("banner") || button.name.Contains("gk2") || button.name.Contains("Banner"))
                continue;

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

            // Skip delete buttons - not essential for accessibility
            if (button.name.Contains("delete") || button.name.Contains("Delete"))
                continue;

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
                    Plugin.Log.LogInfo($"[DiscoverElements] Skipping button '{button.name}' - no valid label");
                    continue;
                }
            }

            Plugin.Log.LogInfo($"[DiscoverElements] Adding button: '{text}' (name: {button.name})");
            Elements.Add(new GUIElement
            {
                Go = button.gameObject,
                Label = text,
                Type = ElementType.Button
            });
        }

        // Inventory-style item cells (BaseItemCellGUI) are not UIButtons, so the loop above
        // misses them. InventoryItemHandler owns that concern — it appends each navigable
        // cell (chest/inventory grids and the autopsy table's flesh/bones/blood extraction
        // grid) as an ItemCell element. Activating one presses the cell.
        InventoryItemHandler.DiscoverItemCells(gui, Elements);

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

        // Fallback: Look for interactive UILabel elements (like save slots, new game button) that don't have UIButton
        var allLabels = gui.GetComponentsInChildren<UILabel>(true);
        foreach (var label in allLabels)
        {
            if (string.IsNullOrWhiteSpace(label.text)) continue;
            var text = ScreenReader.StripNguiCodes(label.text);
            if (string.IsNullOrWhiteSpace(text) || text.Length <= 1) continue;

            // Skip labels that are already part of discovered elements
            if (Elements.Any(e => e.Go == label.gameObject)) continue;

            // Only add if this label or its parent seems clickable/interactive
            var labelGo = label.gameObject;
            var parent = label.transform.parent;

            // Check if parent is likely a clickable container
            bool isClickable = false;
            if (parent != null)
            {
                // Skip non-interactive containers
                if (parent.name.Contains("header") || parent.name.Contains("Header") ||
                    parent.name.Contains("label") || parent.name.Contains("Label"))
                    isClickable = false;
                else
                {
                    // Check parent name patterns for interactive elements
                    isClickable = parent.name.Contains("slot") || parent.name.Contains("Slot") ||
                                 parent.name.Contains("save") || parent.name.Contains("Save") ||
                                 parent.name.Contains("new") || parent.name.Contains("New") ||
                                 parent.name.Contains("game") || parent.name.Contains("Game");

                    // Check if parent has a UIButton in its children (makes it interactive)
                    if (!isClickable && parent.GetComponentInChildren<UIButton>(true) != null)
                        isClickable = true;
                }
            }

            if (isClickable)
            {
                // Add the label GameObject itself as the interactive element
                // Use the parent if available, otherwise use the label's GameObject
                var elementGO = parent?.gameObject ?? label.gameObject;

                // Check if we already have this element
                var existing = Elements.FirstOrDefault(e => e.Label == text);
                if (existing != null)
                {
                    // Replace inactive element with active one
                    if (!existing.Go.activeInHierarchy && elementGO.activeInHierarchy)
                    {
                        Plugin.Log.LogInfo($"[DiscoverElements] Replacing inactive '{text}' with active version");
                        existing.Go = elementGO;
                    }
                }
                else if (!Elements.Any(e => e.Go == elementGO))
                {
                    // Add new element
                    Plugin.Log.LogInfo($"[DiscoverElements] Adding label as button: '{text}' from parent: {parent?.name ?? "null"}");
                    Elements.Add(new GUIElement
                    {
                        Go = elementGO,
                        Label = text,
                        Type = ElementType.Button
                    });
                }
            }
            else
            {
                Plugin.Log.LogInfo($"[DiscoverElements] Skipping label '{text}' - parent not clickable: {parent?.name ?? "null"}");
            }
        }
    }

    internal static List<GUIElement> GetActiveElements()
    {
        return Elements.Where(e => e.Go != null && e.Go.activeInHierarchy).ToList();
    }

    private static void DumpHierarchy(GameObject go, int depth, int maxDepth = 8)
    {
        if (depth > maxDepth) return;

        string indent = new string(' ', depth * 2);
        var active = go.activeInHierarchy ? "✓" : "✗";
        var selfActive = go.activeSelf ? "●" : "○";

        Plugin.Log.LogInfo($"{indent}[{active}{selfActive}] {go.name}");

        // Log UILabel text if present
        var label = go.GetComponent<UILabel>();
        if (label != null && !string.IsNullOrEmpty(label.text))
        {
            Plugin.Log.LogInfo($"{indent}  └─ UILabel: \"{ScreenReader.StripNguiCodes(label.text)}\"");
        }

        // Log UIButton if present
        if (go.GetComponent<UIButton>() != null)
        {
            Plugin.Log.LogInfo($"{indent}  └─ UIButton");
        }

        foreach (Transform child in go.transform)
        {
            DumpHierarchy(child.gameObject, depth + 1, maxDepth);
        }
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

        if (elem.Type == ElementType.ItemCell)
        {
            // Press the item cell, which fires its on-action callback (e.g. the autopsy
            // table's "extract this body part" flow → confirm dialog the mod reads next).
            InventoryItemHandler.PressItemCell(elem.Cell);
            return;
        }

        if (elem.Type == ElementType.Button)
        {
            // Try to find a UIButton component (direct, in children, or parent)
            var button = elem.Go.GetComponent<UIButton>();
            if (button == null)
                button = elem.Go.GetComponentInChildren<UIButton>();
            if (button == null)
                button = elem.Go.GetComponentInParent<UIButton>();

            if (button != null)
            {
                button.SetState(UIButtonColor.State.Pressed, false);
                button.gameObject.SendMessage("OnPress", true, SendMessageOptions.DontRequireReceiver);
                button.gameObject.SendMessage("OnPress", false, SendMessageOptions.DontRequireReceiver);
                button.gameObject.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
                button.SetState(UIButtonColor.State.Normal, false);
            }
            else
            {
                // Fallback: Send messages to the element itself and its children
                elem.Go.SendMessage("OnSlotSelected", SendMessageOptions.DontRequireReceiver);
                elem.Go.SendMessage("Select", SendMessageOptions.DontRequireReceiver);
                elem.Go.SendMessage("OnPress", true, SendMessageOptions.DontRequireReceiver);
                elem.Go.SendMessage("OnPress", false, SendMessageOptions.DontRequireReceiver);
                elem.Go.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);

                foreach (var child in elem.Go.GetComponentsInChildren<Transform>())
                {
                    if (child == elem.Go.transform) continue;
                    child.gameObject.SendMessage("OnSlotSelected", SendMessageOptions.DontRequireReceiver);
                    child.gameObject.SendMessage("Select", SendMessageOptions.DontRequireReceiver);
                    child.gameObject.SendMessage("OnPress", true, SendMessageOptions.DontRequireReceiver);
                    child.gameObject.SendMessage("OnPress", false, SendMessageOptions.DontRequireReceiver);
                    child.gameObject.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
                }
            }
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
            AdjustSlider(elem, -0.1f);
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
            AdjustSlider(elem, 0.1f);
        }
    }

    private static void AdjustSlider(GUIElement elem, float delta)
    {
        float newValue = Mathf.Clamp01(elem.Slider.value + delta);
        elem.Slider.value = newValue;

        var smartSlider = elem.Slider.GetComponent<SmartSlider>();
        if (smartSlider != null)
        {
            smartSlider.OnSliderChanged();
        }

        foreach (var listener in elem.Slider.GetComponentsInChildren<UIProgressBar>())
        {
            listener.value = newValue;
        }

        elem.Go.SendMessage("OnSliderChanged", SendMessageOptions.DontRequireReceiver);
        elem.Slider.gameObject.SendMessage("OnValueChange", newValue, SendMessageOptions.DontRequireReceiver);

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
