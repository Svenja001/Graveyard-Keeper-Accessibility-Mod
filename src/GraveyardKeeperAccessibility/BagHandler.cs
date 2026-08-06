namespace GraveyardKeeperAccessibility;

/// <summary>
/// Bags (Universalbeutel and friends, from the refugee-camp DLC) inside the player's own
/// inventory window.
///
/// A bag is an <see cref="Item"/> whose definition is <c>ItemType.Bag</c>; it carries its own
/// little inventory. The game shows it two ways at once, and neither was reachable by keyboard:
///
///  * every bag you carry gets an inline <see cref="BagInventoryWidget"/> row inside the main
///    inventory panel, so its contents were listed among the loose items with nothing to say they
///    were in a bag;
///  * clicking a bag "opens" it into <see cref="InventoryGUI"/>.bag_panel — a second panel where
///    the real moving happens. While that panel is open, clicking an item in the main panel puts
///    it INTO the bag and clicking one in the bag panel takes it back out.
///
/// The mod's generic inventory activation (use / equip / assign) never took that second path, so
/// Enter on a bag opened it and then nothing could be put in. This class supplies the missing
/// half: panel/bag grouping for the cell list, and the put-in / take-out activation with the
/// game's own move maths (so "doesn't fit", "bag full" and stack pickers all behave natively).
/// </summary>
internal static class BagHandler
{
    private static FieldInfo _openBagField;
    private static MethodInfo _maxMoveCount;

    // ---- state -----------------------------------------------------------------

    /// <summary>The bag currently opened into the side panel, or null when none is open.</summary>
    internal static Item OpenBagIn(InventoryGUI gui)
    {
        if (gui == null) return null;
        try
        {
            _openBagField ??= AccessTools.Field(typeof(InventoryGUI), "_current_open_bag");
            var bag = _openBagField?.GetValue(gui) as Item;
            return (bag == null || bag.IsEmpty()) ? null : bag;
        }
        catch { return null; }
    }

    /// <summary>The bag side panel while it's actually shown, else null.</summary>
    internal static InventoryPanelGUI ActiveBagPanel(BaseGUI gui)
    {
        try
        {
            if (!(gui is InventoryGUI inv)) return null;
            var panel = inv.bag_panel;
            if (panel == null || !panel.gameObject.activeInHierarchy) return null;
            return panel;
        }
        catch { return null; }
    }

    /// <summary>True when the cell lives in the opened-bag side panel rather than the main grid.</summary>
    internal static bool IsBagPanelCell(BaseItemCellGUI cell, BaseGUI gui)
    {
        var panel = ActiveBagPanel(gui);
        if (panel == null || cell == null) return false;
        try { return cell.transform.IsChildOf(panel.transform); }
        catch { return false; }
    }

    /// <summary>
    /// The bag whose inline widget this cell belongs to (the bag rows drawn inside the main
    /// inventory panel), or null for a loose inventory cell.
    /// </summary>
    internal static Item BagOfInlineCell(BaseItemCellGUI cell)
    {
        try
        {
            var widget = cell?.GetComponentInParent<BagInventoryWidget>();
            var data = widget?.inventory_data;
            return (data == null || data.IsEmpty() || !data.is_bag) ? null : data;
        }
        catch { return null; }
    }

    // HasAnyBag walks the whole widget tree, and the cell discovery asks it once per item cell.
    // One answer per window per frame is plenty — the widget set can't change mid-pass.
    private static BaseGUI _hasBagGui;
    private static int _hasBagFrame = -1;
    private static bool _hasBagResult;

    /// <summary>Whether this inventory window has any bag at all — no bag, no extra chatter.</summary>
    internal static bool HasAnyBag(BaseGUI gui)
    {
        if (gui == _hasBagGui && Time.frameCount == _hasBagFrame) return _hasBagResult;
        _hasBagGui = gui;
        _hasBagFrame = Time.frameCount;
        _hasBagResult = ScanForBag(gui);
        return _hasBagResult;
    }

    private static bool ScanForBag(BaseGUI gui)
    {
        try
        {
            if (!(gui is InventoryGUI)) return false;
            if (ActiveBagPanel(gui) != null) return true;
            foreach (var widget in gui.GetComponentsInChildren<BagInventoryWidget>(true))
            {
                if (widget == null || !widget.gameObject.activeInHierarchy) continue;
                var data = widget.inventory_data;
                if (data != null && !data.IsEmpty() && data.is_bag) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Item cells belonging to the opened-bag panel when that panel is NOT a child of the
    /// inventory window (so <c>gui.GetComponentsInChildren</c> would miss them). Returns an
    /// empty list in the normal case where the panel is a descendant and already covered.
    /// </summary>
    internal static List<BaseItemCellGUI> ExtraBagPanelCells(BaseGUI gui)
    {
        var extra = new List<BaseItemCellGUI>();
        try
        {
            var panel = ActiveBagPanel(gui);
            if (panel == null) return extra;
            if (panel.transform.IsChildOf(gui.transform)) return extra;
            extra.AddRange(panel.GetComponentsInChildren<BaseItemCellGUI>(true));
        }
        catch { }
        return extra;
    }

    /// <summary>Same idea as <see cref="ExtraBagPanelCells"/>, for the empty-panel report.</summary>
    internal static InventoryPanelGUI DetachedBagPanel(BaseGUI gui)
    {
        try
        {
            var panel = ActiveBagPanel(gui);
            if (panel == null || panel.transform.IsChildOf(gui.transform)) return null;
            return panel;
        }
        catch { return null; }
    }

    // ---- naming / capacity ------------------------------------------------------

    internal static string BagName(Item bag)
    {
        if (bag == null) return "bag";
        var name = ScreenReader.StripNguiCodes(bag.definition?.GetItemName() ?? bag.id)?.Trim();
        return string.IsNullOrEmpty(name) ? "bag" : name;
    }

    /// <summary>True when the bag holds nothing.</summary>
    internal static bool IsEmpty(Item bag)
    {
        try
        {
            if (bag?.inventory == null) return true;
            foreach (var it in bag.inventory)
                if (it != null && !it.IsEmpty()) return false;
            return true;
        }
        catch { return true; }
    }

    /// <summary>How full a bag is — "empty, 12 slots free" / "3 of 12 slots used".</summary>
    internal static string DescribeCapacity(Item bag)
    {
        try
        {
            if (bag == null) return null;
            int size = bag.inventory_size;
            if (size <= 0 && bag.definition != null)
                size = bag.definition.bag_size_x * bag.definition.bag_size_y;
            if (size <= 0) return null;

            int used = 0;
            if (bag.inventory != null)
                foreach (var it in bag.inventory)
                    if (it != null && !it.IsEmpty()) used++;

            if (used == 0) return $"empty, {size} slots free";
            if (used >= size) return $"full, {size} of {size} slots used";
            return $"{used} of {size} slots used";
        }
        catch { return null; }
    }

    /// <summary>
    /// The line added to the inventory window's own announcement: which bag is open (and how
    /// full), or — with a bag carried but none open — how to get into one. Null when the player
    /// carries no bags at all.
    /// </summary>
    private static bool _saidOpenHint;

    internal static string HeaderFor(BaseGUI gui)
    {
        if (!(gui is InventoryGUI inv)) return null;
        if (!HasAnyBag(gui)) return null;

        var open = OpenBagIn(inv);
        if (open == null)
        {
            // A one-off nudge: Enter-activates-the-row is how the whole mod works, so once the
            // player has heard it there's no reason to repeat it every time they open their bags.
            if (_saidOpenHint) return null;
            _saidOpenHint = true;
            return "Press Enter on a bag to open it";
        }

        var capacity = DescribeCapacity(open);
        var intro = string.IsNullOrEmpty(capacity)
            ? $"{BagName(open)} open"
            : $"{BagName(open)} open, {capacity}";
        return $"{intro}. Enter puts an item in or takes it out, Enter on the bag closes it";
    }

    // ---- activation -------------------------------------------------------------

    /// <summary>
    /// Enter on an item cell in the player's inventory when bags are involved. Handles three
    /// cases the generic use/equip path can't: toggling a bag open or closed, putting an item
    /// into the open bag, and taking one back out.
    /// </summary>
    /// <returns>
    /// handled=false when this isn't a bag situation at all (the caller falls back to use/equip);
    /// otherwise a spoken summary (possibly null, when a count picker took over the screen and
    /// will announce itself) and whether the caller should skip its own refresh.
    /// </returns>
    internal static (string summary, bool skipRefresh, bool handled) Activate(InventoryGUI gui, BaseItemCellGUI cell)
    {
        if (gui == null || cell == null) return (null, false, false);
        var item = cell.item;
        if (item == null || item.IsEmpty()) return (null, false, false);

        var openBag = OpenBagIn(gui);

        // A bag itself: open it, or close it if it's the one already open. We drive OpenBag/
        // CloseBag directly rather than pressing the cell because the game's press path defers
        // the re-open through a 0.01s timer when switching bags — the cell list would then be
        // re-discovered before the new bag's panel existed, and the player would hear nothing.
        if (item.is_bag)
        {
            var name = BagName(item);
            try
            {
                if (openBag == item)
                {
                    gui.CloseBag();
                    return ($"Closed {name}", false, true);
                }

                if (openBag != null) gui.CloseBag();
                gui.OpenBag(item);

                // An empty bag reports its own capacity through the empty-panel line of the
                // refresh that follows, so only say it here when the bag has something in it.
                var capacity = IsEmpty(item) ? null : DescribeCapacity(item);
                var summary = string.IsNullOrEmpty(capacity)
                    ? $"Opened {name}"
                    : $"Opened {name}, {capacity}";
                return ($"{summary}. Enter on an item puts it in or takes it out", false, true);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BAG] toggling '{name}' failed: {ex.Message}");
                return ($"Could not open {name}", false, true);
            }
        }

        // No bag open: nothing bag-ish to do, let the caller use/equip as before.
        if (openBag == null) return (null, false, false);

        var move = Resolve(gui, cell, openBag);
        if (move.reason != null) return (move.reason, true, true);

        // One item (or a non-stacking one) moves straight away; a bigger stack opens the game's
        // amount picker, which is its own window and announces itself — stay silent for that one.
        bool opensPicker = move.max > 1 && (item.definition?.stack_count ?? 1) > 1;

        InventoryItemHandler.PressItemCell(cell);

        if (opensPicker) return (null, true, true);

        return (move.fromBag
            ? $"{move.itemName} taken out of the {move.bagName}"
            : $"{move.itemName} put in the {move.bagName}", false, true);
    }

    /// <summary>
    /// Why this cell can't be moved right now — "does not fit", "the bag is full", "your inventory
    /// is full" — or null when the move would go through. Used by the whole-stack (Shift+Enter)
    /// path, which otherwise presses a dead cell and reports a move that never happened.
    /// </summary>
    internal static string MoveBlockedReason(InventoryGUI gui, BaseItemCellGUI cell)
    {
        var openBag = OpenBagIn(gui);
        if (openBag == null || cell?.item == null || cell.item.IsEmpty()) return null;
        return Resolve(gui, cell, openBag).reason;
    }

    /// <summary>
    /// Everything the put-in / take-out press needs to know: which way the item is going, what to
    /// call the two ends, how many the game would move, and — when that count is zero — the reason
    /// to say instead of pressing a cell that would do nothing.
    /// </summary>
    private static (bool fromBag, string bagName, string itemName, int max, string reason)
        Resolve(InventoryGUI gui, BaseItemCellGUI cell, Item openBag)
    {
        var item = cell.item;
        bool fromBag = IsBagPanelCell(cell, gui);
        var bagName = BagName(openBag);
        var itemName = ScreenReader.StripNguiCodes(item.definition?.GetItemName() ?? item.id)?.Trim();
        if (string.IsNullOrEmpty(itemName)) itemName = item.id;

        // Moving out of one of the OTHER bags you carry (their inline rows stay live while a bag
        // is open, and the game supports bag-to-bag moves). The game reads this off the panel's
        // selected widget; we're acting before that selection exists, so resolve it ourselves.
        Item fromOtherBag = fromBag ? null : BagOfInlineCell(cell);
        if (fromOtherBag == openBag) fromOtherBag = null;

        // Ask the game how much it would actually move. Zero means the press would silently do
        // nothing (or, for a stack, open a 0-to-0 picker) — so say why instead of pressing.
        int max = MaxMoveCount(gui, item, toBag: !fromBag, fromNotOpenBag: fromOtherBag);
        string reason = null;
        if (max <= 0)
        {
            if (!fromBag && !item.CanBeInsertedInBag(openBag))
                reason = $"{itemName} does not fit in the {bagName}";
            else
                reason = fromBag ? "Your inventory is full" : $"The {bagName} is full";
        }

        return (fromBag, bagName, itemName, max, reason);
    }

    /// <summary>
    /// The game's own <c>InventoryGUI.GetMaxMoveCount</c>: how many of this item could move in the
    /// requested direction, accounting for the bag's allowed contents, the room on both sides and
    /// how many you're holding. Returns 0 if the call can't be made, which reads as "can't move".
    /// </summary>
    private static int MaxMoveCount(InventoryGUI gui, Item item, bool toBag, Item fromNotOpenBag)
    {
        try
        {
            _maxMoveCount ??= AccessTools.Method(typeof(InventoryGUI), "GetMaxMoveCount");
            if (_maxMoveCount == null) return 0;
            var result = _maxMoveCount.Invoke(gui, new object[] { item, toBag, false, fromNotOpenBag });
            return result is int count ? count : 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[BAG] move-count check failed: {ex.Message}");
            return 0;
        }
    }
}
