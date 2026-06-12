namespace GraveyardKeeperAccessibility;

internal static class InventoryItemHandler
{
    private static ManualLogSource _log;
    private static BaseGUI _currentInventoryGUI;
    private static HashSet<string> _announcedItems = new();

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _log?.LogInfo("[INVENTORY] InventoryItemHandler initialized");
    }

    internal static void OnGUIOpened(BaseGUI gui)
    {
        if (gui == null) return;

        var guiTypeName = gui.GetType().Name;

        // Detect inventory and chest GUIs
        if (IsInventoryGUI(guiTypeName))
        {
            _currentInventoryGUI = gui;
            _announcedItems.Clear();
            AnnounceAllInventoryItems(gui);
        }
    }

    internal static void OnGUIClosed(BaseGUI gui)
    {
        if (gui == _currentInventoryGUI)
        {
            _currentInventoryGUI = null;
            _announcedItems.Clear();
        }
    }

    internal static void Update()
    {
        if (_currentInventoryGUI == null) return;

        // Check for items that were added/removed
        CheckForItemChanges();
    }

    private static bool IsInventoryGUI(string guiTypeName)
    {
        return guiTypeName.Contains("Inventory") ||
               guiTypeName.Contains("Chest") ||
               guiTypeName.Contains("Storage") ||
               guiTypeName.Contains("Container") ||
               guiTypeName.Contains("Bag");
    }

    private static void AnnounceAllInventoryItems(BaseGUI gui)
    {
        try
        {
            var itemLabels = GetInventoryItemLabels(gui);

            var announcements = new List<string>();
            foreach (var label in itemLabels)
            {
                if (string.IsNullOrWhiteSpace(label)) continue;

                var cleaned = ScreenReader.StripNguiCodes(label).Trim();
                if (!string.IsNullOrEmpty(cleaned) && cleaned.Length > 1)
                {
                    announcements.Add(cleaned);
                    _announcedItems.Add(cleaned);
                }
            }

            string announcement;
            if (announcements.Count > 0)
            {
                announcement = "Items: " + string.Join(", ", announcements);
            }
            else
            {
                announcement = "Empty";
            }

            _log?.LogInfo($"[INVENTORY] {announcement}");
            ScreenReader.Say(announcement);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[INVENTORY] Error announcing items: {ex.Message}");
        }
    }

    private static void CheckForItemChanges()
    {
        try
        {
            if (_currentInventoryGUI == null) return;

            var currentItems = GetInventoryItemLabels(_currentInventoryGUI);
            var newItems = new List<string>();

            foreach (var item in currentItems)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;

                var cleaned = ScreenReader.StripNguiCodes(item).Trim();
                if (!string.IsNullOrEmpty(cleaned) && cleaned.Length > 1)
                {
                    if (!_announcedItems.Contains(cleaned))
                    {
                        newItems.Add(cleaned);
                        _announcedItems.Add(cleaned);
                    }
                }
            }

            // Check for removed items
            var removedItems = _announcedItems.Where(item =>
            {
                var currentText = string.Join(" ", currentItems
                    .Select(l => ScreenReader.StripNguiCodes(l).Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l)));
                return !currentText.Contains(item);
            }).ToList();

            if (newItems.Count > 0)
            {
                var announcement = "Added: " + string.Join(", ", newItems);
                _log?.LogInfo($"[INVENTORY] {announcement}");
                ScreenReader.Say(announcement);
            }

            if (removedItems.Count > 0)
            {
                var announcement = "Removed: " + string.Join(", ", removedItems);
                _log?.LogInfo($"[INVENTORY] {announcement}");
                ScreenReader.Say(announcement);

                foreach (var item in removedItems)
                    _announcedItems.Remove(item);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[INVENTORY] Error checking for changes: {ex.Message}");
        }
    }

    private static List<string> GetInventoryItemLabels(BaseGUI gui)
    {
        var items = new List<string>();
        try
        {
            // Find all UILabel components that might represent items
            var allLabels = gui.GetComponentsInChildren<UILabel>(true);

            foreach (var label in allLabels)
            {
                if (label == null || !label.gameObject.activeInHierarchy) continue;
                if (string.IsNullOrWhiteSpace(label.text)) continue;

                // Filter out UI chrome (buttons, headers, labels)
                var parentName = label.transform.parent?.name ?? "";
                var labelName = label.name;

                // Skip common UI elements that aren't items
                if (parentName.Contains("header") || parentName.Contains("Header") ||
                    parentName.Contains("title") || parentName.Contains("Title") ||
                    labelName.Contains("header") || labelName.Contains("Header") ||
                    labelName.Contains("label") && !labelName.Contains("ItemLabel"))
                    continue;

                // Look for item-like text patterns
                var text = label.text;

                // Items typically have names and sometimes quantities in parentheses
                // e.g., "Shovel (2)" or "Wood"
                if (text.Length > 1 && !text.StartsWith("[") && !text.Equals("X", StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(text);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[INVENTORY] Error getting item labels: {ex.Message}");
        }

        return items;
    }

    // ---- Item-cell navigation (shared with GUIAccessibility) --------------------
    // Inventory/craft item cells are BaseItemCellGUI, not UIButtons, so GUIAccessibility's
    // button discovery misses them. These helpers let the menu navigator expose item cells
    // as navigable elements — covering chest/inventory grids and the autopsy table's
    // body-part extraction grid (cut out flesh/bones/blood).

    /// <summary>
    /// Find every non-empty, active item cell in the GUI and append it to the navigator's
    /// element list so the player can arrow to it and activate it.
    /// </summary>
    internal static void DiscoverItemCells(BaseGUI gui, List<GUIElement> elements)
    {
        try
        {
            // Collect cells separately so we can group them by their owning panel. In a chest
            // the player needs to know which items are in the chest (to take) versus in their
            // own inventory (to put); a flat, unlabeled list hides that distinction.
            var discovered = new List<GUIElement>();

            foreach (var cell in gui.GetComponentsInChildren<BaseItemCellGUI>(true))
            {
                if (cell == null || !cell.gameObject.activeInHierarchy) continue;
                if (cell.id_empty) continue;
                if (elements.Any(e => e.Go == cell.gameObject)) continue;
                if (discovered.Any(e => e.Go == cell.gameObject)) continue;

                var label = DescribeItemCell(cell);
                if (string.IsNullOrEmpty(label)) continue;

                var (panel, rank) = GetPanelContext(cell, gui);
                if (!string.IsNullOrEmpty(panel))
                    label = $"{panel}: {label}";

                discovered.Add(new GUIElement
                {
                    Go = cell.gameObject,
                    Label = label,
                    Type = ElementType.ItemCell,
                    Cell = cell,
                    SortRank = rank
                });
            }

            // Stable sort: chest items first, then the player's inventory.
            foreach (var elem in discovered.OrderBy(e => e.SortRank))
            {
                _log?.LogInfo($"[INVENTORY] Adding item cell: '{elem.Label}'");
                elements.Add(elem);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[INVENTORY] Error discovering item cells: {ex.Message}");
        }
    }

    /// <summary>
    /// Determine which inventory panel an item cell belongs to and a sort rank for ordering.
    /// For a chest, the chest side ("Chest", rank 0) sorts before the player side
    /// ("Inventory", rank 1). Other two-panel GUIs fall back to the panel's own title.
    /// </summary>
    private static (string label, int rank) GetPanelContext(BaseItemCellGUI cell, BaseGUI gui)
    {
        try
        {
            var panel = cell.GetComponentInParent<InventoryPanelGUI>();
            if (panel == null) return (null, 2);

            if (gui is ChestGUI chest)
            {
                if (panel == chest.chest_panel) return ("Chest", 0);
                if (panel == chest.player_panel) return ("Inventory", 1);
            }

            return (PanelLabel(panel, gui), 2);
        }
        catch
        {
            return (null, 2);
        }
    }

    /// <summary>Spoken name for an inventory panel: "Chest"/"Inventory" for a chest, else its title.</summary>
    private static string PanelLabel(InventoryPanelGUI panel, BaseGUI gui)
    {
        if (panel == null) return null;
        if (gui is ChestGUI chest)
        {
            if (panel == chest.chest_panel) return "Chest";
            if (panel == chest.player_panel) return "Inventory";
        }
        var title = ScreenReader.StripNguiCodes(panel.panel_title?.text)?.Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    /// <summary>
    /// For multi-panel inventory GUIs (chest, etc.), describe which panels hold no items so the
    /// player knows e.g. the chest is empty even though their own inventory isn't. Returns null
    /// when nothing's empty or the GUI isn't panel-based.
    /// </summary>
    internal static string DescribeEmptyPanels(BaseGUI gui)
    {
        try
        {
            var empties = new List<string>();
            foreach (var panel in gui.GetComponentsInChildren<InventoryPanelGUI>(true))
            {
                if (panel == null || !panel.gameObject.activeInHierarchy) continue;

                bool hasItems = panel.GetComponentsInChildren<BaseItemCellGUI>(true)
                    .Any(c => c != null && c.gameObject.activeInHierarchy && !c.id_empty);
                if (hasItems) continue;

                empties.Add($"{PanelLabel(panel, gui) ?? "Inventory"} empty");
            }

            return empties.Count > 0 ? string.Join(", ", empties) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Spoken label for an item cell: the localized item name, plus the stack count when
    /// more than one. Returns null for empty/unnamed cells.
    /// </summary>
    internal static string DescribeItemCell(BaseItemCellGUI cell)
    {
        try
        {
            var item = cell.item;
            if (item == null || item.IsEmpty()) return null;

            // The autopsy grid includes a pseudo-item cell for inserting a part into the
            // body; its raw name is unreadable, so give it a clear spoken label.
            if (item.id == "insertion_button_pseudoitem")
                return "Insert body part";

            var name = ScreenReader.StripNguiCodes(item.definition?.GetItemName() ?? "").Trim();
            if (string.IsNullOrEmpty(name)) name = item.id;
            if (string.IsNullOrEmpty(name)) return null;

            return item.value > 1 ? $"{name}, {item.value}" : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Activate an item cell — fires its on-action callback (e.g. the autopsy table's
    /// "extract this body part" flow → confirm dialog).
    /// </summary>
    /// <remarks>
    /// <see cref="BaseItemCellGUI.OnPressed"/> runs the cell's action first, then plays a
    /// click sound by dereferencing <c>container.selection.gameObject</c>. For cells outside
    /// a fully-initialized inventory widget (e.g. some CraftGUI cells) <c>container.selection</c>
    /// is null, so that last line throws AFTER the real action already ran. Swallow it here so
    /// the exception never bubbles up into Plugin.Update and abort the rest of the frame.
    /// </remarks>
    internal static void PressItemCell(BaseItemCellGUI cell)
    {
        if (cell == null) return;
        try
        {
            cell.OnPressed(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INVENTORY] item cell press threw after action (harmless): {ex.Message}");
        }
    }
}