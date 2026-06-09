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
}