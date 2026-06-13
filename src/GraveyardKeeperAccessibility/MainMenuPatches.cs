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
    internal int SortRank;

    // Set for elements discovered from BaseMenuGUI rows (options/main/in-game menus).
    // These let us drive the game's own widgets directly instead of guessing at
    // dec/inc buttons, which is both cleaner and avoids announcing rows twice.
    internal MenuItemGUI MenuItem;
    internal SimpleOptionsSwitcher OptionsSwitcher;
    internal SmartSlider Smart;

    internal string ReadLabel()
    {
        if (OptionsSwitcher != null && OptionsSwitcher.label != null)
        {
            var val = ScreenReader.StripNguiCodes(OptionsSwitcher.label.text);
            if (!string.IsNullOrWhiteSpace(val))
                return Label + ": " + val;
        }

        if (Smart != null)
            return Label + ": " + Smart.value;

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
        var active = GetActiveElements();
        var cellCount = active.Count(e => e.Type == ElementType.ItemCell);
        var header = cellCount > 0 ? $"{guiName}, {cellCount} items" : guiName;

        // For multi-panel inventory GUIs (e.g. a chest), call out any side that's empty so the
        // player knows the chest or their own inventory has nothing in it.
        var emptyDesc = InventoryItemHandler.DescribeEmptyPanels(gui);
        if (!string.IsNullOrEmpty(emptyDesc))
            header += $". {emptyDesc}";

        // Auto-focus the first entry so the player lands on something immediately instead of
        // having to press down once to enter the list.
        if (active.Count > 0)
        {
            SelectedIndex = 0;
            ScreenReader.Say($"{header}. {active[0].ReadLabel()}");
        }
        else
        {
            ScreenReader.Say(header);
        }
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
        // BaseMenuGUI screens (main menu, in-game menu, options) are built from MenuItemGUI
        // rows, each of which is a button, an options switcher, or a slider. Discovering those
        // rows directly maps cleanly onto our element types and — crucially — avoids the
        // generic heuristic below picking up each row a second time (the duplicate-read bug).
        // SaveSlotsMenuGUI is also a BaseMenuGUI but has no MenuItemGUI rows (its slots are
        // plain labels), so only take this path when rows actually exist.
        if (gui is BaseMenuGUI menu)
        {
            var menuItems = menu.GetComponentsInChildren<MenuItemGUI>(true);
            if (menuItems.Length > 0)
            {
                DiscoverMenuItems(menu, menuItems);
                return;
            }
        }

        // The build-desk catalog reuses the regular CraftGUI in "build" mode. Its generic
        // UIButtons are all anonymous "Anfertigen" (Craft) buttons, so the heuristic below
        // would list four identical "Anfertigen" entries with no clue what you're building.
        // Instead, enumerate the CraftItemGUI rows directly and label each with the build's
        // name + whether you can afford it. Activating a row presses its cell, which the game
        // routes to CraftBuilding -> placement (handled by BuildPlacementHandler).
        if (gui is CraftGUI craftGui && MainGame.me?.build_mode_logics?.IsBuilding() == true)
        {
            DiscoverBuildCatalogItems(craftGui);
            return;
        }

        // The vendor (trade) screen is built from two inventory panels plus two offer
        // widgets, and its confirm/cancel are UIButtons whose children are named only
        // "btn"/"spr" — meaningless to read out. Discover the item cells (labelled
        // Buy/Sell/offer by InventoryItemHandler) and add clearly-named Confirm/Cancel rows.
        if (gui is VendorGUI vendor)
        {
            DiscoverVendor(vendor);
            return;
        }

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

    // Build the element list for a BaseMenuGUI from its MenuItemGUI rows. Each row is a
    // slider, an options switcher, or a plain button; we drive the game's own widget so the
    // behaviour matches mouse/gamepad exactly. A row is only listed once.
    private static void DiscoverMenuItems(BaseMenuGUI menu, MenuItemGUI[] items)
    {
        Plugin.Log.LogInfo($"[DiscoverMenuItems] Found {items.Length} MenuItemGUI rows in {menu.GetType().Name}");

        foreach (var mi in items)
        {
            if (mi == null) continue;

            var switcher = mi.GetComponentInChildren<SimpleOptionsSwitcher>(true);
            var smart = mi.GetComponentInChildren<SmartSlider>(true);
            var title = GetMenuItemTitle(mi, smart, switcher);

            if (switcher != null)
            {
                Elements.Add(new GUIElement
                {
                    Go = mi.gameObject,
                    Label = title,
                    Type = ElementType.Switcher,
                    MenuItem = mi,
                    OptionsSwitcher = switcher
                });
            }
            else if (smart != null)
            {
                Elements.Add(new GUIElement
                {
                    Go = mi.gameObject,
                    Label = title,
                    Type = ElementType.Slider,
                    MenuItem = mi,
                    Smart = smart,
                    Slider = smart.GetComponentInChildren<UISlider>(true)
                });
            }
            else
            {
                Elements.Add(new GUIElement
                {
                    Go = mi.gameObject,
                    Label = title,
                    Type = ElementType.Button,
                    MenuItem = mi
                });
            }
        }

        // Some menus carry a plain back/close UIButton that isn't a MenuItemGUI row. Pick those
        // up too, but skip anything already represented by a row or the promo banners.
        foreach (var button in menu.GetComponentsInChildren<UIButton>(true))
        {
            if (button == null) continue;
            var name = button.name;
            if (name.Contains("banner") || name.Contains("gk2") || name.Contains("Banner")) continue;
            if (Elements.Any(e => e.Go == button.gameObject || button.transform.IsChildOf(e.Go.transform))) continue;

            var label = ScreenReader.StripNguiCodes(button.GetComponentInChildren<UILabel>()?.text);
            if (string.IsNullOrWhiteSpace(label)) label = name;
            if (string.IsNullOrWhiteSpace(label) || label.Length <= 1) continue;

            Elements.Add(new GUIElement
            {
                Go = button.gameObject,
                Label = label,
                Type = ElementType.Button
            });
        }
    }

    // The visible title of a menu row, ignoring any label that belongs to the row's slider or
    // switcher (those hold the current value, not the row name). Falls back to the localized
    // token the game uses, then the GameObject name.
    private static string GetMenuItemTitle(MenuItemGUI mi, SmartSlider smart, SimpleOptionsSwitcher switcher)
    {
        foreach (var label in mi.GetComponentsInChildren<UILabel>(true))
        {
            if (label == null) continue;
            if (smart != null && label.transform.IsChildOf(smart.transform)) continue;
            if (switcher != null && label.transform.IsChildOf(switcher.transform)) continue;

            var text = ScreenReader.StripNguiCodes(label.text)?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        if (!string.IsNullOrEmpty(mi.locale_token))
        {
            var loc = ScreenReader.StripNguiCodes(GJL.L(mi.locale_token) ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(loc)) return loc;
        }

        return mi.name;
    }

    // CraftItemGUI.CanCraft(int? amount) is private; cache the MethodInfo so we can tell the
    // player whether a build option is currently affordable.
    private static MethodInfo _canCraftMethod;

    /// <summary>
    /// List the build-desk catalog as one navigable row per buildable object, named and with
    /// an affordability hint. Each row's cell, when activated, fires the same action a mouse
    /// click would (CraftItemGUI.OnItemAction -> OnCraft -> CraftBuilding).
    /// </summary>
    private static void DiscoverBuildCatalogItems(CraftGUI craftGui)
    {
        var items = craftGui.GetItemsList();
        if (items == null)
        {
            Plugin.Log.LogWarning("[BUILD] CraftGUI has no items list");
            return;
        }

        int added = 0;
        foreach (var cri in items)
        {
            if (cri == null || !cri.gameObject.activeInHierarchy) continue;
            var cell = cri.item_gui;
            if (cell == null) continue;

            Elements.Add(new GUIElement
            {
                Go = cri.gameObject,
                Label = BuildCatalogLabel(cri),
                Type = ElementType.ItemCell,
                Cell = cell
            });
            added++;
        }

        Plugin.Log.LogInfo($"[BUILD] Catalog discovered {added} build option(s)");
    }

    /// <summary>Spoken label for a build catalog row: the object name + affordability.</summary>
    private static string BuildCatalogLabel(CraftItemGUI cri)
    {
        string name = null;
        try
        {
            // In build mode CraftItemGUI.Redraw sets label_name to the localized object name.
            name = ScreenReader.StripNguiCodes(cri.label_name?.text)?.Trim();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var def = cri.craft_definition;
                if (def != null)
                    name = ScreenReader.StripNguiCodes(GJL.L(def.GetNameNonLocalized()) ?? "").Trim();
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(name)) name = "Build option";

        return CanBuild(cri) ? name : $"{name}, not enough materials";
    }

    private static bool CanBuild(CraftItemGUI cri)
    {
        try
        {
            // Prefer the build-zone inventory check (matches what placement enforces).
            var def = cri.craft_definition;
            var logics = MainGame.me?.build_mode_logics;
            if (def != null && logics != null)
                return logics.CanBuild(def);

            _canCraftMethod ??= AccessTools.Method(typeof(CraftItemGUI), "CanCraft", new[] { typeof(int?) });
            if (_canCraftMethod != null)
                return (bool)_canCraftMethod.Invoke(cri, new object[] { null });
        }
        catch { }
        return true;
    }

    /// <summary>
    /// Build the navigable element list for the vendor (trade) screen: every item cell
    /// (vendor stock, the player's inventory, and both offer widgets — labelled Buy/Sell/
    /// Your offer/Vendor offer by InventoryItemHandler), followed by clearly-named
    /// "Confirm trade" and "Cancel offer" buttons. Activating an item moves it into/out of
    /// an offer; activating Confirm accepts the assembled offer.
    /// </summary>
    private static void DiscoverVendor(VendorGUI vendor)
    {
        InventoryItemHandler.DiscoverItemCells(vendor, Elements);

        AddVendorButton(vendor.btn_confirm, "Confirm trade");
        AddVendorButton(vendor.btn_cancel, "Cancel offer");

        Plugin.Log.LogInfo($"[VENDOR] Discovered {Elements.Count} element(s)");
    }

    private static void AddVendorButton(UIButton button, string label)
    {
        if (button == null) return;
        if (Elements.Any(e => e.Go == button.gameObject)) return;

        Elements.Add(new GUIElement
        {
            Go = button.gameObject,
            Label = label,
            Type = ElementType.Button
        });
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

    // Re-discover the current GUI's elements in place (used after a chest move mutates the
    // grids) and keep focus near where it was, re-announcing the now-current row.
    private static void RefreshCurrentGUI(int focusIndex)
    {
        if (_currentGUI == null) return;

        Elements.Clear();
        DiscoverElements(_currentGUI);

        // After a move a side may have just become empty — mention it so the player knows.
        var emptyDesc = InventoryItemHandler.DescribeEmptyPanels(_currentGUI);

        var active = GetActiveElements();
        if (active.Count == 0)
        {
            SelectedIndex = -1;
            ScreenReader.Say(string.IsNullOrEmpty(emptyDesc) ? "Empty" : emptyDesc);
            return;
        }

        SelectedIndex = Mathf.Clamp(focusIndex, 0, active.Count - 1);
        var label = active[SelectedIndex].ReadLabel();
        ScreenReader.Say(string.IsNullOrEmpty(emptyDesc) ? label : $"{emptyDesc}. {label}");
    }

    // After a vendor move the offer/stock grids are redrawn in place, so re-discover the
    // element list (like RefreshCurrentGUI) and keep focus near where it was. Lead the
    // announcement with the new running balance so the player hears the cost/gain of the
    // deal they're building, then the now-current row.
    private static void RefreshVendorAfterMove(VendorGUI vendor, int focusIndex)
    {
        Elements.Clear();
        DiscoverElements(vendor);

        string balance = null;
        try
        {
            if (vendor.trading != null)
                balance = DescribeBalance(vendor.trading.GetTotalBalance());
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[VENDOR] balance read failed: {ex.Message}");
        }

        var active = GetActiveElements();
        if (active.Count == 0)
        {
            SelectedIndex = -1;
            ScreenReader.Say(balance ?? "Empty");
            return;
        }

        SelectedIndex = Mathf.Clamp(focusIndex, 0, active.Count - 1);
        var row = active[SelectedIndex].ReadLabel();
        ScreenReader.Say(string.IsNullOrEmpty(balance) ? row : $"{balance}. {row}");
    }

    // Spoken running balance of the assembled offer. GetTotalBalance is the player's net:
    // positive means the player gains money (selling), negative means they pay (buying).
    private static string DescribeBalance(float balance)
    {
        if (Mathf.Abs(balance) < 0.005f) return "Even trade";
        var amount = MoneyToSpeech(Mathf.Abs(balance));
        return balance > 0f ? $"You receive {amount}" : $"You pay {amount}";
    }

    // Decompose a money value into spoken gold/silver/bronze, matching Trading.FormatMoney's
    // arithmetic (1 gold = 100 silver, 1 silver = 100 bronze) but voiced as words instead of
    // the (gld)/(slv)/(brz) coin sprites the on-screen label uses.
    private static string MoneyToSpeech(float value)
    {
        value = Mathf.Round(Mathf.Abs(value) * 100f) / 100f;
        int gold = Mathf.FloorToInt(value / 100f);
        int silver = Mathf.FloorToInt(value - gold * 100f);
        int bronze = Mathf.RoundToInt((value - gold * 100f - silver) * 100f);

        var parts = new List<string>();
        if (gold > 0) parts.Add($"{gold} gold");
        if (silver > 0) parts.Add($"{silver} silver");
        if (bronze > 0) parts.Add($"{bronze} bronze");
        return parts.Count > 0 ? string.Join(", ", parts) : "nothing";
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
            var prevIndex = SelectedIndex;
            InventoryItemHandler.PressItemCell(elem.Cell);

            // A chest moves the item and redraws both grids in place — same GUI, but our
            // cached cell list is now stale. Re-discover so the player isn't navigating
            // ghost cells, and keep them on a sensible row. (Stackable items instead open a
            // count picker, a new GUI that the normal flow will announce.)
            if (_currentGUI is ChestGUI)
                RefreshCurrentGUI(prevIndex);
            else if (_currentGUI is VendorGUI vendor)
                RefreshVendorAfterMove(vendor, prevIndex);
            return;
        }

        if (elem.Type == ElementType.Button)
        {
            // Menu rows fire their configured action directly — matches a mouse click and
            // plays the same click sound.
            if (elem.MenuItem != null)
            {
                elem.MenuItem.OnMenuItemSelect();
                return;
            }

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
        if (elem.Type == ElementType.Switcher && elem.OptionsSwitcher != null)
        {
            elem.OptionsSwitcher.Dec();
            ScreenReader.Say(elem.ReadLabel());
        }
        else if (elem.Type == ElementType.Switcher && elem.DecButton != null)
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
        if (elem.Type == ElementType.Switcher && elem.OptionsSwitcher != null)
        {
            elem.OptionsSwitcher.Inc();
            ScreenReader.Say(elem.ReadLabel());
        }
        else if (elem.Type == ElementType.Switcher && elem.IncButton != null)
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

        // Menu sliders carry the SmartSlider directly; otherwise look for one on the UISlider.
        var smartSlider = elem.Smart ?? elem.Slider.GetComponent<SmartSlider>();
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
