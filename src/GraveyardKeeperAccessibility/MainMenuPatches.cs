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

    // When set, ActivateSelected runs this directly instead of guessing at a UIButton /
    // SendMessage. Used for buttons whose action we can call straight on the game object
    // (e.g. the vendor's Confirm/Cancel, which map to VendorGUI.FinishOffer / ResetOrder).
    internal Action OnActivate;

    // Custom Left/Right handlers, used by the multiquality crafting view to cycle an
    // ingredient's quality. When set, AdjustLeft/AdjustRight invoke these (and the handler
    // does its own announcing) instead of driving dec/inc buttons.
    internal Action OnAdjustLeft;
    internal Action OnAdjustRight;

    // Computed label, re-evaluated on every read. Used for rows whose text changes in place
    // (e.g. an ingredient whose quality the player cycles with Left/Right). Takes precedence
    // over the static Label/ValueLabel reading below.
    internal Func<string> ReadDynamic;

    // Set for save-slot rows in SaveSlotsMenuGUI. Enter calls OnSlotSelect (load/new game);
    // the Delete key calls OnDeletePressed. This avoids the generic UIButton fallback picking
    // up the slot's child delete_button and wiring Enter to deletion instead of loading.
    internal SaveSlotGUI SaveSlot;

    internal string ReadLabel()
    {
        if (ReadDynamic != null)
        {
            var dyn = ReadDynamic();
            if (!string.IsNullOrWhiteSpace(dyn)) return dyn;
        }

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

    // The amount/price picker (ItemCountGUI) changes its SmartSlider on Left/Right via the
    // game's own key handling, so stepping it ourselves would double-count. Instead we watch
    // its value each frame and announce changes. _watchedSmart is non-null only while such a
    // picker is open.
    private static SmartSlider _watchedSmart;
    private static int _watchedValue;

    // The picker's total-price function (null when the picker has no price, e.g. a chest
    // count). Read from ItemCountGUI._price_calculate_delegate so we can voice the exact
    // running total instead of the coin-sprite label, which doesn't convert to speech.
    private static ItemCountGUI.PriceCalculateDelegate _watchedPrice;
    private static FieldInfo _priceDelegateField;

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

        // New-technology popup: read the full unlock text, then land on the first button (OK /
        // unlock) so Enter confirms. Must run before the generic "Dialog" branch below, which
        // would otherwise read only the tech name and return.
        if (gui is TechUnlockDialogGUI techUnlock)
        {
            AnnounceTechUnlock(techUnlock);
            return;
        }

        // Tutorial windows: read the whole instruction text, then focus Close (or a help topic).
        if (gui is TutorialGUI || gui is BaseTutorialGUI)
        {
            AnnounceTutorial(gui);
            return;
        }

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

        // The amount/price picker: announce the item and starting amount with a hint, and
        // start watching the slider so each Left/Right step (handled by the game) is voiced.
        if (gui is ItemCountGUI countGui)
        {
            _watchedSmart = countGui.GetComponentInChildren<SmartSlider>(true);
            _watchedValue = _watchedSmart != null ? _watchedSmart.value : 0;
            _watchedPrice = ReadPriceDelegate(countGui);

            var item = ScreenReader.StripNguiCodes(countGui.header_label?.text)?.Trim();
            var msg = string.IsNullOrEmpty(item) ? "Choose amount" : $"{item}, amount {_watchedValue}";
            ScreenReader.Say($"{msg}{DescribePrice(_watchedValue)}. Left and right to change, Enter to confirm");

            // Still focus the confirm button so Enter works without arrowing.
            var els = GetActiveElements();
            SelectedIndex = els.Count > 0 ? 0 : -1;
            return;
        }

        // The NPCs/quests tab: announce how many characters are listed (the generic header
        // would read the type name "NPCsList") and focus the first card so the player lands
        // on a character to arrow through immediately.
        if (gui is NPCsListGUI)
        {
            var npcCards = GetActiveElements();
            var npcHeader = npcCards.Count == 1
                ? "NPCs and quests, 1 character"
                : $"NPCs and quests, {npcCards.Count} characters";

            if (npcCards.Count > 0)
            {
                SelectedIndex = 0;
                ScreenReader.Say($"{npcHeader}. {npcCards[0].ReadLabel()}");
            }
            else
            {
                ScreenReader.Say($"{npcHeader}. No characters known yet");
            }
            return;
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
        _watchedSmart = null;
        _watchedPrice = null;

        InventoryItemHandler.OnGUIClosed(gui);
    }

    // Voice changes to a watched amount/price slider (the game steps it on Left/Right; we
    // only announce). Called every frame from Plugin.Update while a GUI is active.
    internal static void UpdateWatchers()
    {
        if (_watchedSmart == null) return;
        try
        {
            var v = _watchedSmart.value;
            if (v != _watchedValue)
            {
                _watchedValue = v;
                ScreenReader.Say($"{v}{DescribePrice(v)}");
            }
        }
        catch
        {
            _watchedSmart = null;
        }
    }

    // Read the picker's private total-price function once on open. Returns null when the
    // picker has no price (then nothing extra is spoken).
    private static ItemCountGUI.PriceCalculateDelegate ReadPriceDelegate(ItemCountGUI gui)
    {
        try
        {
            _priceDelegateField ??= AccessTools.Field(typeof(ItemCountGUI), "_price_calculate_delegate");
            return _priceDelegateField?.GetValue(gui) as ItemCountGUI.PriceCalculateDelegate;
        }
        catch
        {
            return null;
        }
    }

    // ", total 80 bronze" for the given amount, or "" when this picker has no price.
    private static string DescribePrice(int amount)
    {
        if (_watchedPrice == null) return "";
        try
        {
            return $", total {MoneyToSpeech(_watchedPrice(amount))}";
        }
        catch
        {
            return "";
        }
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

        // The "new technology unlocked" popup. It's built from a header, an unlocks list and a
        // DialogButtonsGUI (OK / unlock / cancel). The generic UIButton path would find the
        // buttons, but OnGUIOpened's "Dialog" branch reads only the first label and returns, so
        // the unlock text and the other buttons are lost. Expose the dialog buttons explicitly;
        // the full text is spoken by AnnounceTechUnlock.
        if (gui is TechUnlockDialogGUI)
        {
            DiscoverDialogButtons(gui);
            return;
        }

        // Tutorial pop-ups (TutorialGUI, opened automatically as you progress) and the help-topics
        // list (TutorialWindowsGUI). The instruction body is plain UILabels, not buttons, so the
        // generic path finds only a stray button and never the text. List the help topics (if any)
        // and always add an explicit Close; AnnounceTutorial reads the whole body.
        if (gui is TutorialGUI || gui is BaseTutorialGUI)
        {
            DiscoverTutorialButtons(gui);
            return;
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

        // The regular crafting window (anvil, workbench, alchemy bench, ... — anything opened
        // by walking up to a station and pressing E) also lists its recipes as CraftItemGUI
        // rows, not UIButtons, so the generic heuristic below finds only the close button. This
        // is what blocks e.g. repairing a sword at the anvil. Enumerate the recipe rows (and any
        // category tabs) directly: each row reads its name, ingredients and whether you can
        // afford it; activating it crafts (presses the row's cell -> OnItemAction -> OnCraft).
        if (gui is CraftGUI regularCraftGui)
        {
            DiscoverCraftItems(regularCraftGui);
            return;
        }

        // The "resource based" crafting station (anvil repair, decompose-for-science, the
        // sharpening/processing benches, ...) works differently from CraftGUI: instead of a
        // recipe list you first pick the item to process from a separate resource picker, then
        // the matching recipe's extra materials appear and a single craft button finishes it.
        // The entry point is the "main ingredient" slot, which starts EMPTY — so the generic
        // item-cell discovery (which skips empty cells) finds only the close/craft buttons and
        // the player has no way to start. This is exactly what blocks repairing the sword at the
        // smith's anvil. Expose the pick slot, the required materials and the craft button.
        if (gui is ResourceBasedCraftGUI resourceCraftGui)
        {
            DiscoverResourceBasedCraft(resourceCraftGui);
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

        // The save-slots screen lists SaveSlotGUI rows (a "new game" entry plus one per save).
        // Each row owns a child delete_button UIButton, so the generic fallbacks below would
        // wire Enter to deletion. Discover the rows explicitly: Enter loads, Delete deletes.
        if (gui is SaveSlotsMenuGUI saveMenu)
        {
            DiscoverSaveSlots(saveMenu);
            return;
        }

        // The NPCs/quests tab (opened with N) lists NPCItemGUI cards — each a character's
        // name, description, relationship score and their active quest objectives. These are
        // info displays, not buttons, so the generic heuristic below finds nothing navigable.
        // Make each card a navigable row whose label reads out the whole card.
        if (gui is NPCsListGUI npcsList)
        {
            DiscoverNPCsList(npcsList);
            return;
        }

        // The technology tree (opened with T) draws its techs as a scrollable graph of
        // TechTreeGUIItem nodes spread across branches — invisible to a screen reader, and the
        // generic heuristic finds only the close button. List every branch as a "Category" row
        // (Enter switches branch) followed by one row per tech in the current branch, each
        // reading its name, what it unlocks, cost and state; Enter buys an available tech (the
        // game's own confirm dialog then opens). See DiscoverTechTree.
        if (gui is TechTreeGUI techTree)
        {
            DiscoverTechTree(techTree);
            return;
        }

        // The grave menu (E on a grave). It shows the buried body plus the grave's cross and
        // fence — both wear down over time and are restored with a repair kit. A sighted player
        // clicks the fence/cross icon to open its repair/replace craft; those icons are
        // BaseItemCellGUI cells with no readable label, so the generic path finds only the
        // extract/close buttons. List each grave part as a navigable row whose label reads its
        // condition; Enter opens the part's fix craft (the regular CraftGUI, already accessible).
        if (gui is GraveGUI grave)
        {
            DiscoverGraveParts(grave);
            return;
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

    // Build one element per active save slot. The first row is the "new game" entry; the rest
    // are existing saves. We label each from the slot's own labels (name / in-game day / stats /
    // real-world save time) and keep a reference to the SaveSlotGUI so Enter can load and Delete
    // can delete, both via the game's own methods.
    private static void DiscoverSaveSlots(SaveSlotsMenuGUI menu)
    {
        var slots = menu.GetComponentsInChildren<SaveSlotGUI>(true);
        Plugin.Log.LogInfo($"[DiscoverSaveSlots] Found {slots.Length} SaveSlotGUI rows");

        foreach (var slot in slots)
        {
            if (slot == null) continue;
            // Skip the inactive prefab the menu copies from; only real rows are active.
            if (!slot.gameObject.activeInHierarchy) continue;

            var parts = new List<string>();
            void Add(UILabel l)
            {
                if (l == null) return;
                var t = ScreenReader.StripNguiCodes(l.text)?.Trim();
                if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
            }
            Add(slot.slot_name);
            Add(slot.txt_descr);
            Add(slot.txt_stats);
            Add(slot.txt_realtime);

            var label = parts.Count > 0 ? string.Join(", ", parts) : slot.name;

            Elements.Add(new GUIElement
            {
                Go = slot.gameObject,
                Label = label,
                Type = ElementType.Button,
                SaveSlot = slot
            });
        }
    }

    // Build one navigable row per known NPC shown in the NPCs/quests tab. Each row's label
    // reads the card the game drew: the character's name, relationship score (0–100), short
    // description, and every not-yet-complete quest objective tied to them. The cards aren't
    // clickable, so these rows have no action — Up/Down simply reads each character in turn.
    private static void DiscoverNPCsList(NPCsListGUI gui)
    {
        var cards = gui.GetComponentsInChildren<NPCItemGUI>(true);
        Plugin.Log.LogInfo($"[DiscoverNPCsList] Found {cards.Length} NPCItemGUI cards");

        foreach (var card in cards)
        {
            if (card == null) continue;
            // The list keeps an inactive prefab it clones from; only real cards are active.
            if (!card.gameObject.activeInHierarchy) continue;

            Elements.Add(new GUIElement
            {
                Go = card.gameObject,
                Label = DescribeNPCCard(card),
                Type = ElementType.Button
            });
        }
    }

    // Spoken description of one NPC card: "name, relationship N. description. Quests: a. b."
    // Reads the labels the game already populated (NPCItemGUI.Draw) so we don't re-derive
    // localization, and skips the relationship for the player's own card (which hides it).
    private static string DescribeNPCCard(NPCItemGUI card)
    {
        var parts = new List<string>();

        var name = ScreenReader.StripNguiCodes(card.npc_name?.text)?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = card.name;

        var relation = ScreenReader.StripNguiCodes(card.relation_txt?.text)?.Trim();
        // The relationship is the 0–100 reputation the game draws as a bare number; spell out
        // "out of 100" so it isn't mistaken for a quest counter like "0 of 10".
        if (card.go_relation != null && card.go_relation.activeInHierarchy && !string.IsNullOrWhiteSpace(relation))
            parts.Add($"{name}, relationship {relation} out of 100");
        else
            parts.Add(name);

        // The six sin-NPCs (astrologer, inquisitor, snake/cultist, merchant, actress, bishop) each
        // carry a day icon (top_icon_txt = ObjectDefinition.day_icon, e.g. "(d3)") naming the
        // weekday tied to their sin. We translate it to a readable German day via the linked NPC's
        // id, but only when the game ITSELF draws a day icon — everyone else has an empty icon and
        // gets no day line. NOTE: this is the NPC's *associated* day, NOT a visiting schedule —
        // the Snake (npc_cultist, "(d3)") is talkable at his spot every day despite showing Tag
        // des Neids — so we phrase it "Zugehöriger Tag", not "Besuchstag"/"appears on".
        bool gameShowsDay = !string.IsNullOrWhiteSpace(card.top_icon_txt?.text);
        var npcId = GetLinkedNpcId(card);
        var visitingDay = DayTimeAnnouncer.VisitingDayForNpc(npcId);
        if (gameShowsDay && !string.IsNullOrEmpty(visitingDay))
            parts.Add($"Zugehöriger Tag: {visitingDay}");

        var descr = ScreenReader.StripNguiCodes(card.npc_descr?.text)?.Trim();
        if (!string.IsNullOrWhiteSpace(descr) && descr.IndexOf('!') < 0)
            parts.Add(descr);

        var quests = new List<string>();
        foreach (var q in card.GetComponentsInChildren<NPCListQuestText>(true))
        {
            if (q == null || !q.gameObject.activeInHierarchy) continue;
            var text = ScreenReader.StripNguiCodes(q.txt?.text)?.Trim();
            // GJL.L echoes "!task_x!" when a task has no translation — skip those.
            if (!string.IsNullOrWhiteSpace(text) && text.IndexOf('!') < 0)
                quests.Add(text);
        }
        if (quests.Count > 0)
            parts.Add($"Quests: {string.Join(". ", quests)}");

        return string.Join(". ", parts);
    }

    // NPCItemGUI keeps the character it drew in a private _linked_npc field; read its npc_id so
    // we can name the weekday a visiting NPC appears on. Returns null if it can't be resolved.
    private static FieldInfo _linkedNpcField;
    private static string GetLinkedNpcId(NPCItemGUI card)
    {
        try
        {
            _linkedNpcField ??= AccessTools.Field(typeof(NPCItemGUI), "_linked_npc");
            var npc = _linkedNpcField?.GetValue(card) as KnownNPC;
            return npc?.npc_id;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NPCs] could not read linked npc id: {ex.Message}");
            return null;
        }
    }

    // --- New-technology popup & tutorial windows ---------------------------------------------

    // Expose a tech-unlock dialog's buttons (OK / unlock / cancel) as navigable rows. Each is a
    // DialogButtonGUI; activating it calls the game's own OnClick, which fires the configured
    // delegate (buy the tech, reveal it, or just close). The unlock text is spoken separately.
    private static void DiscoverDialogButtons(BaseGUI gui)
    {
        foreach (var btn in gui.GetComponentsInChildren<DialogButtonGUI>(true))
        {
            if (btn == null || !btn.gameObject.activeInHierarchy) continue;
            var label = ScreenReader.StripNguiCodes(btn.GetComponentInChildren<UILabel>()?.text)?.Trim();
            if (string.IsNullOrWhiteSpace(label)) continue;

            var captured = btn;
            Elements.Add(new GUIElement
            {
                Go = btn.gameObject,
                Label = label,
                Type = ElementType.Button,
                OnActivate = () =>
                {
                    try { captured.OnClick(); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[TECH] button click failed: {ex.Message}"); }
                }
            });
        }
    }

    // Speak the entire new-technology popup: the "you've unlocked" subheader (when shown), the
    // tech name, every unlock's name and description, and the cost (for the optional buy variant).
    // Then land focus on the first button so Enter confirms without arrowing.
    private static void AnnounceTechUnlock(TechUnlockDialogGUI gui)
    {
        var parts = new List<string>();
        void Add(string s)
        {
            var t = ScreenReader.StripNguiCodes(s)?.Trim();
            if (!string.IsNullOrWhiteSpace(t) && t.Length > 1 && t.IndexOf('!') < 0)
                parts.Add(t);
        }

        if (gui.subheader != null && gui.subheader.gameObject.activeInHierarchy)
            Add(gui.subheader.text);
        if (gui.label_header != null)
            Add(gui.label_header.text);

        foreach (var unlock in gui.GetComponentsInChildren<TechTreeGUIUnlockItem>(true))
        {
            if (unlock == null || !unlock.gameObject.activeInHierarchy) continue;
            Add(unlock.label_name?.text);
            if (unlock.label_description != null && unlock.label_description.gameObject.activeInHierarchy)
                Add(unlock.label_description.text);
        }

        if (gui.label_cost != null && gui.label_cost.gameObject.activeInHierarchy)
        {
            var cost = ScreenReader.StripNguiCodes(gui.label_cost.text)?.Trim();
            if (!string.IsNullOrWhiteSpace(cost))
                parts.Add($"Cost {cost}");
        }

        var body = string.Join(". ", parts);
        var active = GetActiveElements();
        SelectedIndex = active.Count > 0 ? 0 : -1;

        if (active.Count > 0)
            ScreenReader.Say(string.IsNullOrEmpty(body) ? active[0].ReadLabel() : $"{body}. {active[0].ReadLabel()}");
        else
            ScreenReader.Say(string.IsNullOrEmpty(body) ? "New technology unlocked" : body);
    }

    // Build navigable rows for a tutorial window. A content pop-up (TutorialGUI) has no real
    // buttons — it closes on a key press — so we add an explicit Close. The help-topics list
    // (TutorialWindowsGUI) instead lists each TutorialItemGUI topic; activating one opens it.
    private static void DiscoverTutorialButtons(BaseGUI gui)
    {
        // Help-topics list: each topic is a TutorialItemGUI (not a UIButton). Activating it
        // opens that topic's tutorial window.
        foreach (var item in gui.GetComponentsInChildren<TutorialItemGUI>(true))
        {
            if (item == null || !item.gameObject.activeInHierarchy) continue;
            if (Elements.Any(e => e.Go == item.gameObject)) continue;

            var captured = item;
            var label = ScreenReader.StripNguiCodes(item.GetComponentInChildren<UILabel>()?.text)?.Trim();
            if (string.IsNullOrWhiteSpace(label)) label = item.name;
            Elements.Add(new GUIElement
            {
                Go = item.gameObject,
                Label = label,
                Type = ElementType.Button,
                OnActivate = () =>
                {
                    try { captured.OnTutorialItemSelect(); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[TUTORIAL] topic select failed: {ex.Message}"); }
                }
            });
        }

        // Always offer an explicit keyboard close (GameKey.Select has no key binding, so the
        // native "press to dismiss" never fires for us). OnClosePressed is the same path the
        // window's own select/back uses, so it dismisses cleanly and resumes any pending flow.
        Elements.Add(new GUIElement
        {
            Go = gui.gameObject,
            Label = "Close",
            Type = ElementType.Button,
            OnActivate = () =>
            {
                try { gui.OnClosePressed(); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[TUTORIAL] close failed: {ex.Message}"); }
            }
        });
    }

    // Speak the full body text of a tutorial window (all visible instruction labels, in order,
    // de-duplicated) and then land on the first navigable row (a help topic, or Close).
    private static void AnnounceTutorial(BaseGUI gui)
    {
        var body = CollectVisibleText(gui);
        var active = GetActiveElements();
        SelectedIndex = active.Count > 0 ? 0 : -1;

        var lead = string.IsNullOrEmpty(body) ? "Tutorial" : body;
        if (active.Count > 0)
            ScreenReader.Say($"{lead}. {active[SelectedIndex].ReadLabel()}");
        else
            ScreenReader.Say(lead);
    }

    // Concatenate every visible UILabel in a window (skipping labels that belong to a discovered
    // button row, and untranslated "!token!" markers) into one spoken string. Used to read the
    // whole body of tutorial windows, whose instructions are plain labels rather than buttons.
    private static string CollectVisibleText(BaseGUI gui)
    {
        var seen = new HashSet<string>();
        var parts = new List<string>();
        foreach (var label in gui.GetComponentsInChildren<UILabel>(true))
        {
            if (label == null || !label.gameObject.activeInHierarchy) continue;

            // Skip labels that are part of a navigable button row, but NOT the synthetic Close
            // (whose Go is the whole window — matching on it would exclude every label).
            if (Elements.Any(e => e.Type == ElementType.Button && e.Go != gui.gameObject &&
                    (label.transform == e.Go.transform || label.transform.IsChildOf(e.Go.transform))))
                continue;

            var t = ScreenReader.StripNguiCodes(label.text)?.Trim();
            if (string.IsNullOrWhiteSpace(t) || t.Length < 2 || t.IndexOf('!') >= 0) continue;
            if (seen.Add(t)) parts.Add(t);
        }
        return string.Join(". ", parts);
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
        // The build catalog includes a special "_remove_" row: activating it switches the desk
        // into demolish mode (handled by BuildPlacementHandler). It has no materials, so skip the
        // Requires/affordability suffixes and give it a clear action hint instead.
        try
        {
            if (cri.craft_definition?.id == "_remove_")
            {
                var rname = ScreenReader.StripNguiCodes(cri.label_name?.text)?.Trim();
                if (string.IsNullOrWhiteSpace(rname)) rname = "Remove object";
                return $"{rname}. Enter to choose what to demolish";
            }
        }
        catch { }

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

        var label = name;
        var needs = CraftNeedsText(cri);
        if (!string.IsNullOrEmpty(needs)) label += $". Requires {needs}";
        label += CanBuild(cri) ? ". Ready" : ". Not enough materials";
        return label;
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
    /// List a regular crafting station's recipes as navigable rows: any category tabs first
    /// (Enter switches tab), then one row per recipe in the current tab. Each recipe row reads
    /// its name, ingredients and affordability; activating it crafts (presses the row's cell,
    /// which the game routes to CraftItemGUI.OnItemAction -> OnCraft).
    /// </summary>
    private static void DiscoverCraftItems(CraftGUI craftGui)
    {
        var items = craftGui.GetItemsList();
        if (items == null)
        {
            Plugin.Log.LogWarning("[CRAFT] CraftGUI has no items list");
            return;
        }

        // A star-quality recipe can be "expanded" into a detailed view where you pick each
        // ingredient's quality before crafting. While one is expanded, present only its
        // ingredient/quality controls (drill-in), not the recipe list.
        var expanded = items.FirstOrDefault(c => c != null && c.full_detailed_view);
        if (expanded != null)
        {
            DiscoverCraftDetailedView(craftGui, expanded);
            return;
        }

        DiscoverCraftTabs(craftGui);

        int added = 0;
        foreach (var cri in items)
        {
            // Only rows in the active tab are activeInHierarchy — this naturally filters to the
            // currently selected category, so switching tabs (and re-discovering) lists the right set.
            if (cri == null || !cri.gameObject.activeInHierarchy) continue;
            var cell = cri.item_gui;
            if (cell == null) continue;

            // Star-quality recipes don't craft on Enter — they open a detailed quality picker.
            // Route those through our own expand handler so we can re-list the controls and land
            // focus sensibly; simple recipes keep the default cell-press (= craft).
            var captured = cri;
            Action onActivate = IsMultiquality(cri)
                ? () => ExpandRecipe(craftGui, captured)
                : (Action)null;

            Elements.Add(new GUIElement
            {
                Go = cri.gameObject,
                Label = CraftRecipeLabel(cri),
                Type = ElementType.ItemCell,
                Cell = cell,
                OnActivate = onActivate
            });
            added++;
        }

        // A window with no listed recipes is silent and baffling to a blind player. Most often
        // it's a broken object whose repair is still tech-locked (e.g. the broken beehive). Add a
        // single readable line explaining the situation so Up/Down lands on something to hear.
        if (added == 0)
        {
            var info = EmptyCraftWindowInfo(craftGui);
            if (!string.IsNullOrEmpty(info))
            {
                Elements.Add(new GUIElement
                {
                    Go = craftGui.gameObject,
                    Label = info,
                    Type = ElementType.Button,
                    OnActivate = () => ScreenReader.Say(info)
                });
            }
        }

        Plugin.Log.LogInfo($"[CRAFT] discovered {added} recipe(s)");
    }

    /// <summary>
    /// The resource-based crafting station (anvil repair, science decompose, sharpening bench,
    /// ...): a pick slot, the chosen recipe's extra materials and one craft button. The flow is
    /// (1) activate the pick slot -> a resource picker opens (a CraftResourcesSelectGUI, handled
    /// generically as an item grid) -> choose the item; (2) the station re-draws with the item in
    /// the slot and its required materials; (3) activate the craft button to do the work.
    /// </summary>
    private static void DiscoverResourceBasedCraft(ResourceBasedCraftGUI gui)
    {
        // The pick slot. Pressing it fires the cell's select callback (OnChooseItem), which opens
        // the resource picker; CheckForNewGUI then announces that picker. When an item is already
        // chosen the slot shows it, and pressing it again re-opens the picker to change it.
        var main = gui.main_ingredient;
        if (main != null)
        {
            string PickLabel()
            {
                if (main.id_empty)
                {
                    var hint = ScreenReader.StripNguiCodes(gui.label_resourse_hint?.text)?.Trim();
                    return string.IsNullOrEmpty(hint) ? "Choose item" : $"Choose item. {hint}";
                }
                var chosen = InventoryItemHandler.DescribeItemCell(main);
                return string.IsNullOrEmpty(chosen)
                    ? "Choose item"
                    : $"Selected {chosen}. Enter to choose a different item";
            }

            Elements.Add(new GUIElement
            {
                Go = main.gameObject,
                Label = PickLabel(),
                ReadDynamic = PickLabel,
                Type = ElementType.ItemCell,
                Cell = main
            });
        }

        // Once an item is picked the recipe's extra materials are drawn into the ingredient
        // cells. They aren't interactive — read them out as the materials the craft consumes so
        // the player knows what's needed (the craft button reports whether they have enough).
        if (gui.ingredients != null)
        {
            foreach (var ing in gui.ingredients)
            {
                if (ing == null || ing == main) continue;
                // Don't skip is_inactive_state here: a requirement cell greys out precisely when
                // the player is short of it (e.g. not enough faith), and that's exactly the case
                // we most need to read out — skipping it is why a too-costly study just said
                // "not enough materials" with no clue what was missing.
                if (!ing.gameObject.activeInHierarchy || ing.id_empty) continue;
                var desc = InventoryItemHandler.DescribeItemCell(ing);
                // Faith ("Glaube") and study points ("Wissenschaft") usually carry a localized
                // name, but some point icons strip to nothing — name those from the item id as a
                // fallback so a cost is never silent.
                if (string.IsNullOrEmpty(desc))
                {
                    var sn = SpecialNeedName(ing.item?.id);
                    if (!string.IsNullOrEmpty(sn))
                    {
                        var v = ing.item?.value ?? 0;
                        desc = v > 1 ? $"{v} {sn}" : sn;
                    }
                }
                if (string.IsNullOrEmpty(desc)) continue;
                var shortNote = ing.is_inactive_state ? ", you don't have enough" : "";
                var label = $"Requires {desc}{shortNote}";
                Elements.Add(new GUIElement
                {
                    Go = ing.gameObject,
                    Label = label,
                    Type = ElementType.Button,
                    OnActivate = () => ScreenReader.Say(label)
                });
            }
        }

        // The craft button. Its UIButton.isEnabled mirrors the game's CanCraft check (an item is
        // chosen and the player has the materials), so use it both to label availability and to
        // gate the press. OnCraftButtonPressed is the game's own handler (repair / decompose).
        if (gui.craft_button != null)
        {
            bool CanCraftNow() => gui.craft_button.ui_button != null && gui.craft_button.ui_button.isEnabled;
            string CraftLabel()
            {
                var verb = ScreenReader.StripNguiCodes(gui.label_craft_btn?.text)?.Trim();
                if (string.IsNullOrEmpty(verb)) verb = "Craft";
                if (CanCraftNow()) return verb;
                // The individual requirement cells above already read each cost (faith, points,
                // materials) and flag the ones you're short of, so keep the button itself terse —
                // repeating the full cost here is what caused the duplicate "Glaube … 1 faith".
                return main != null && main.id_empty
                    ? $"{verb}, choose an item first"
                    : $"{verb}, not enough materials";
            }

            Elements.Add(new GUIElement
            {
                Go = gui.craft_button.gameObject,
                Label = CraftLabel(),
                ReadDynamic = CraftLabel,
                Type = ElementType.Button,
                OnActivate = () =>
                {
                    if (!CanCraftNow())
                    {
                        ScreenReader.Say(main != null && main.id_empty
                            ? "Choose an item first"
                            : "Not enough materials");
                        return;
                    }
                    try { gui.OnCraftButtonPressed(); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[RESCRAFT] craft failed: {ex.Message}"); }
                    // The craft is queued/performed; the station usually stays open. Re-read so
                    // affordability and the slot update (the item may have been consumed).
                    var verb = ScreenReader.StripNguiCodes(gui.label_craft_btn?.text)?.Trim();
                    ScreenReader.Say(string.IsNullOrEmpty(verb) ? "Crafting" : verb);
                    if (_currentGUI is ResourceBasedCraftGUI)
                        RefreshCurrentGUI(SelectedIndex);
                }
            });
        }

        // Close button.
        if (gui.close_btn != null)
        {
            Elements.Add(new GUIElement
            {
                Go = gui.close_btn,
                Label = "Close",
                Type = ElementType.Button
            });
        }

        Plugin.Log.LogInfo($"[RESCRAFT] discovered station, {Elements.Count} element(s)");
    }

    /// <summary>
    /// The expanded star-quality view: one row per ingredient (Left/Right cycles its quality
    /// when switchable), a read-out of the predicted output quality, a Craft action, and a
    /// Back entry that returns to the recipe list.
    /// </summary>
    private static void DiscoverCraftDetailedView(CraftGUI craftGui, CraftItemGUI cri)
    {
        var needs = cri.current_craft?.needs;
        var cells = GetIngredientCells(cri);
        int count = needs?.Count ?? 0;

        for (int i = 0; i < count; i++)
        {
            int idx = i;
            BaseItemCellGUI cell = (cells != null && i < cells.Length) ? cells[i] : null;
            var go = cell != null ? cell.gameObject : cri.gameObject;
            bool switchable = IsSwitchableIngredient(cri, i);

            var elem = new GUIElement
            {
                Go = go,
                Type = switchable ? ElementType.Switcher : ElementType.Button,
                Label = "Ingredient",
                ReadDynamic = () => IngredientLabel(cri, idx, cell, switchable)
            };
            if (switchable)
            {
                // CraftItemGUI.OnChangeIngredient uses step +1 = "previous", -1 = "next"; map
                // Right -> next, Left -> previous so the order feels natural, and re-announce.
                elem.OnAdjustRight = () => { ChangeIngredient(cri, idx, -1); AnnounceFocused(); };
                elem.OnAdjustLeft = () => { ChangeIngredient(cri, idx, 1); AnnounceFocused(); };
            }
            Elements.Add(elem);
        }

        Elements.Add(new GUIElement
        {
            Go = cri.gameObject,
            Type = ElementType.Button,
            Label = "Predicted quality",
            ReadDynamic = () => PredictedQualityLabel(cri),
            OnActivate = () => ScreenReader.Say(PredictedQualityLabel(cri))
        });

        Elements.Add(new GUIElement
        {
            Go = cri.gameObject,
            Type = ElementType.Button,
            Label = $"Craft {OutputName(cri)}" + (CanCraftRecipe(cri) ? "" : $", {CraftUnavailableReason(cri).ToLowerInvariant()}"),
            OnActivate = () => CraftMultiquality(craftGui, cri)
        });

        Elements.Add(new GUIElement
        {
            Go = cri.gameObject,
            Type = ElementType.Button,
            Label = "Back to recipe list",
            OnActivate = () => CollapseDetailedView(craftGui, cri)
        });

        Plugin.Log.LogInfo($"[CRAFT] detailed view: {count} ingredient(s)");
    }

    private static void AnnounceFocused()
    {
        var active = GetActiveElements();
        if (SelectedIndex >= 0 && SelectedIndex < active.Count)
            ScreenReader.Say(active[SelectedIndex].ReadLabel());
    }

    /// <summary>Enter the star-quality picker for a recipe and read out its first control.</summary>
    private static void ExpandRecipe(CraftGUI craftGui, CraftItemGUI cri)
    {
        try { craftGui.ExpandItem(cri); }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CRAFT] expand failed: {ex.Message}"); }

        Elements.Clear();
        DiscoverElements(craftGui);

        var active = GetActiveElements();
        SelectedIndex = active.Count > 0 ? 0 : -1;
        var lead = $"Choosing quality for {OutputName(cri)}";
        ScreenReader.Say(active.Count > 0 ? $"{lead}. {active[0].ReadLabel()}" : lead);
    }

    /// <summary>Leave the star-quality picker and return focus to the recipe in the list.</summary>
    private static void CollapseDetailedView(CraftGUI craftGui, CraftItemGUI cri)
    {
        try { craftGui.CollapseItem(cri); }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CRAFT] collapse failed: {ex.Message}"); }

        Elements.Clear();
        DiscoverElements(craftGui);

        var active = GetActiveElements();
        var idx = active.FindIndex(e => e.Go == cri.gameObject);
        SelectedIndex = idx >= 0 ? idx : (active.Count > 0 ? 0 : -1);
        if (SelectedIndex >= 0)
            ScreenReader.Say($"Recipe list. {active[SelectedIndex].ReadLabel()}");
    }

    /// <summary>Craft the expanded recipe at the chosen ingredient qualities.</summary>
    private static void CraftMultiquality(CraftGUI craftGui, CraftItemGUI cri)
    {
        if (!CanCraftRecipe(cri))
        {
            ScreenReader.Say(CraftUnavailableReason(cri));
            return;
        }

        try
        {
            _onCraftPressedMethod ??= AccessTools.Method(typeof(CraftItemGUI), "OnCraftPressed");
            _onCraftPressedMethod?.Invoke(cri, null);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CRAFT] craft failed: {ex.Message}"); }

        // Stay in the detailed view (the game keeps it open) so the player can craft again,
        // possibly at a different quality. Re-read the now-current affordability.
        Elements.Clear();
        DiscoverElements(craftGui);
        var active = GetActiveElements();
        SelectedIndex = Mathf.Clamp(SelectedIndex, 0, Mathf.Max(0, active.Count - 1));
        ScreenReader.Say($"Crafting {OutputName(cri)}");
    }

    // --- Multiquality reflection helpers (CraftItemGUI internals) ---
    private static FieldInfo _ingredientsField;
    private static FieldInfo _multiqualityIdsField;
    private static MethodInfo _onChangeIngredientMethod;
    private static MethodInfo _isSwitchableMethod;
    private static MethodInfo _onCraftPressedMethod;

    private static bool IsMultiquality(CraftItemGUI cri)
    {
        try { return cri.current_craft?.IsMultiqualityOutput() == true; }
        catch { return false; }
    }

    private static BaseItemCellGUI[] GetIngredientCells(CraftItemGUI cri)
    {
        try
        {
            _ingredientsField ??= AccessTools.Field(typeof(CraftItemGUI), "_ingredients");
            return _ingredientsField?.GetValue(cri) as BaseItemCellGUI[];
        }
        catch { return null; }
    }

    private static List<string> GetMultiqualityIds(CraftItemGUI cri)
    {
        try
        {
            _multiqualityIdsField ??= AccessTools.Field(typeof(CraftItemGUI), "_multiquality_ids");
            return _multiqualityIdsField?.GetValue(cri) as List<string>;
        }
        catch { return null; }
    }

    private static bool IsSwitchableIngredient(CraftItemGUI cri, int index)
    {
        try
        {
            _isSwitchableMethod ??= AccessTools.Method(typeof(CraftItemGUI), "IsSwitchableIngredient", new[] { typeof(int) });
            if (_isSwitchableMethod != null)
                return (bool)_isSwitchableMethod.Invoke(cri, new object[] { index });
        }
        catch { }
        return false;
    }

    private static void ChangeIngredient(CraftItemGUI cri, int index, int step)
    {
        try
        {
            _onChangeIngredientMethod ??= AccessTools.Method(typeof(CraftItemGUI), "OnChangeIngredient", new[] { typeof(int), typeof(int) });
            _onChangeIngredientMethod?.Invoke(cri, new object[] { index, step });
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CRAFT] change ingredient failed: {ex.Message}"); }
    }

    /// <summary>
    /// Spoken label for one ingredient in the detailed view: the chosen variant's name + quality
    /// + required amount. The chosen variant lives in CraftItemGUI._multiquality_ids (the cell's
    /// own item stays the base "group" item, so reading the cell would miss the picked quality).
    /// </summary>
    private static string IngredientLabel(CraftItemGUI cri, int index, BaseItemCellGUI cell, bool switchable)
    {
        var needs = cri.current_craft?.needs;
        var need = (needs != null && index < needs.Count) ? needs[index] : null;

        var ids = GetMultiqualityIds(cri);
        string id = (ids != null && index < ids.Count) ? ids[index] : null;
        if (string.IsNullOrEmpty(id)) id = need?.id;

        string desc = DescribeItemId(id) ?? "ingredient";

        // Required amount (the recipe's per-craft need, e.g. "2 malt, gold quality").
        try
        {
            if (need != null && need.value > 1) desc = $"{need.value} {desc}";
        }
        catch { }

        return switchable ? $"{desc}. Left or right to change quality" : desc;
    }

    /// <summary>Resolve an item id to "name" or "name, tier quality" (for star items).</summary>
    private static string DescribeItemId(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var def = GameBalance.me.GetDataOrNull<ItemDefinition>(id);
            var name = ScreenReader.StripNguiCodes(def?.GetItemName() ?? id)?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = id;

            if (def != null && def.quality_type == ItemDefinition.QualityType.Stars)
            {
                int stars = Mathf.FloorToInt(def.quality);
                string tier = stars switch
                {
                    1 => "bronze quality",
                    2 => "silver quality",
                    3 => "gold quality",
                    _ => stars > 3 ? $"{stars} stars" : null
                };
                if (!string.IsNullOrEmpty(tier)) name = $"{name}, {tier}";
            }
            return name;
        }
        catch { return id; }
    }

    /// <summary>Predicted output-quality odds for the current ingredient choices.</summary>
    private static string PredictedQualityLabel(CraftItemGUI cri)
    {
        try
        {
            var ids = GetMultiqualityIds(cri);
            var result = cri.current_craft.GetMultiqualityResult(ids);
            var p = result.quality_probabilities;
            if (p != null && p.Length >= 3)
            {
                int bronze = Mathf.RoundToInt(p[0] * 100f);
                int silver = Mathf.RoundToInt(p[1] * 100f);
                int gold = Mathf.RoundToInt(p[2] * 100f);
                return $"Predicted quality: bronze {bronze} percent, silver {silver} percent, gold {gold} percent";
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CRAFT] predicted quality failed: {ex.Message}"); }
        return "Predicted quality unavailable";
    }

    private static string OutputName(CraftItemGUI cri)
    {
        try
        {
            var def = cri.current_craft;
            if (def != null)
            {
                var n = ScreenReader.StripNguiCodes(GJL.L(def.GetNameNonLocalized()) ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(n)) return n;
            }
        }
        catch { }
        try { return ScreenReader.StripNguiCodes(cri.label_name?.text)?.Trim() ?? "item"; }
        catch { return "item"; }
    }

    /// <summary>Add each visible category tab as a navigable button; Enter switches to it.</summary>
    private static void DiscoverCraftTabs(CraftGUI craftGui)
    {
        CraftTabGUI[] tabs;
        try { tabs = craftGui.GetComponentsInChildren<CraftTabGUI>(false); }
        catch { return; }
        if (tabs == null || tabs.Length < 2) return; // a single (or no) tab isn't worth navigating

        foreach (var tab in tabs)
        {
            if (tab == null || !tab.gameObject.activeInHierarchy) continue;
            if (Elements.Any(e => e.Go == tab.gameObject)) continue;

            var captured = tab;
            Elements.Add(new GUIElement
            {
                Go = tab.gameObject,
                Label = $"Category: {CraftTabLabel(tab)}",
                Type = ElementType.Button,
                OnActivate = () => SwitchCraftTab(craftGui, captured)
            });
        }
    }

    /// <summary>Switch the crafting window to a tab, then re-list and announce its recipes.</summary>
    private static void SwitchCraftTab(CraftGUI craftGui, CraftTabGUI tab)
    {
        try { tab.OnTabClicked(); }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CRAFT] tab switch failed: {ex.Message}"); }

        Elements.Clear();
        DiscoverElements(craftGui);

        var active = GetActiveElements();
        var name = CraftTabLabel(tab);
        if (active.Count == 0)
        {
            SelectedIndex = -1;
            ScreenReader.Say($"{name}. Empty");
            return;
        }

        // Land on the first recipe so the player immediately hears the tab's contents.
        var idx = active.FindIndex(e => e.Type == ElementType.ItemCell);
        SelectedIndex = idx >= 0 ? idx : 0;
        ScreenReader.Say($"{name}. {active[SelectedIndex].ReadLabel()}");
    }

    private static string CraftTabLabel(CraftTabGUI tab)
    {
        var id = tab?.tab_id ?? "";
        if (id.StartsWith("?")) id = id.Substring(1);
        if (string.IsNullOrEmpty(id)) return "Other";
        try
        {
            var loc = ScreenReader.StripNguiCodes(GJL.L("tab_" + id) ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(loc) && !loc.Contains("!") && loc != "tab_" + id)
                return loc;
        }
        catch { }
        return id;
    }

    /// <summary>Spoken label for a recipe row: name, ingredients, and whether it's craftable.</summary>
    private static string CraftRecipeLabel(CraftItemGUI cri)
    {
        string name = null;
        try
        {
            var def = cri.current_craft;
            if (def != null)
                name = ScreenReader.StripNguiCodes(GJL.L(def.GetNameNonLocalized()) ?? "").Trim();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = ScreenReader.StripNguiCodes(cri.label_name?.text)?.Trim(); }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(name)) name = "Recipe";

        var label = name;

        // A craft with no item output that swaps the object for another (change_wgo) doesn't make
        // an item — it rebuilds the station itself. On a broken object that's its repair, so say so
        // rather than just naming the result (e.g. "Repair: Beehive" not a bare "Beehive").
        try
        {
            var cd = cri.current_craft;
            if (cd != null && !string.IsNullOrEmpty(cd.change_wgo) && cd.GetFirstRealOutput() == null)
                label = $"Repair: {name}";

            // The grave-part window (OpenAsGrave) builds two synthetic crafts whose ids don't
            // localize: a "fix_grave_craft_<part>" repair and a "_remove_" removal. Name them.
            if (cd != null)
            {
                if (!string.IsNullOrEmpty(cd.id) && cd.id.StartsWith("fix_grave_craft_"))
                    label = cd.id.EndsWith("cross") ? "Repair cross" : "Repair fence";
                else if (cd.custom_name == "_remove_")
                    label = "Remove part";
            }
        }
        catch { }

        var needs = CraftNeedsText(cri);
        if (!string.IsNullOrEmpty(needs)) label += $". Requires {needs}";
        label += CanCraftRecipe(cri) ? ". Ready" : $". {CraftUnavailableReason(cri)}";
        // Star-quality recipes open a quality picker on Enter rather than crafting directly.
        if (IsMultiquality(cri)) label += ". Enter to choose quality";
        return label;
    }

    /// <summary>Just the recipe's display name (no "Requires…/Ready" tail), or null.</summary>
    private static string RecipeDisplayName(CraftItemGUI cri)
    {
        if (cri == null) return null;
        try
        {
            var def = cri.current_craft;
            if (def != null)
            {
                var n = ScreenReader.StripNguiCodes(GJL.L(def.GetNameNonLocalized()) ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(n)) return n;
            }
        }
        catch { }
        try { return ScreenReader.StripNguiCodes(cri.label_name?.text)?.Trim(); }
        catch { return null; }
    }

    /// <summary>
    /// What to say after a craft closes the station window. If the station is now running a timed
    /// craft (e.g. a furnace smelt) report it as started; otherwise the craft already finished
    /// instantly (adding fuel, a quick craft) so report it as crafted. A craft that yields a real
    /// item drops it on the ground beside the station (CraftComponent drops player-craft output),
    /// so point the player at it — they can't see it land and would otherwise lose track of it.
    /// </summary>
    private static string CraftStartedMessage(CraftGUI craftGui, CraftDefinition craft, string recipeName)
    {
        try
        {
            var wgo = _crafteryWgoField?.GetValue(craftGui) as WorldGameObject;
            var station = wgo?.components?.craft;
            if (station != null && station.is_crafting && station.current_craft != null)
            {
                string outName = null;
                try { outName = ScreenReader.StripNguiCodes(station.current_craft.GetFirstRealOutput()?.definition?.GetItemName() ?? "").Trim(); }
                catch { }
                if (string.IsNullOrWhiteSpace(outName)) outName = recipeName;
                int pct = Mathf.RoundToInt(Mathf.Clamp01(wgo.progress) * 100f);
                if (string.IsNullOrWhiteSpace(outName)) return "Crafting started";
                return pct >= 1 ? $"Crafting {outName}, {pct} percent done" : $"Started crafting {outName}";
            }
        }
        catch { }

        // Instant craft, already finished. If it made a real item it's now on the ground.
        bool dropsItem = false;
        try { dropsItem = craft?.GetFirstRealOutput() != null; } catch { }
        if (string.IsNullOrWhiteSpace(recipeName)) return "Crafted";
        return dropsItem
            ? $"{recipeName} crafted. It dropped on the ground, press E to pick it up"
            : $"{recipeName} crafted";
    }

    /// <summary>
    /// Whether the player can currently afford this recipe. Uses CraftItemGUI.CanCraft (which
    /// checks the player's inventory), NOT <see cref="CanBuild"/> — that one consults the build
    /// zone's stock and only applies while placing buildings.
    /// </summary>
    private static bool CanCraftRecipe(CraftItemGUI cri)
    {
        try
        {
            _canCraftMethod ??= AccessTools.Method(typeof(CraftItemGUI), "CanCraft", new[] { typeof(int?) });
            if (_canCraftMethod != null)
                return (bool)_canCraftMethod.Invoke(cri, new object[] { null });
        }
        catch { }
        return true;
    }

    /// <summary>
    /// Why a recipe can't be crafted right now, as spoken text. The game's CanCraft collapses every
    /// shortfall into one boolean, so a furnace craft that has all its ingredients but no fuel would
    /// otherwise read as the misleading "not enough materials". Fuel ("fire") lives in the craft's
    /// needs_from_wgo and is stored on the station (wgo.data), not the player's bags — so check it
    /// separately and, when the materials are all present and only the fuel is short, say "No fuel".
    /// </summary>
    private static string CraftUnavailableReason(CraftItemGUI cri)
    {
        try
        {
            var craft = cri?.current_craft;
            if (craft?.needs_from_wgo != null && craft.needs_from_wgo.Count > 0)
            {
                bool needsFire = craft.needs_from_wgo.Any(n => n != null && n.id == "fire");
                var wgo = cri.craft_gui_interface?.GetCrafteryWGO();
                if (needsFire && wgo?.data != null && !wgo.data.IsEnoughItems(craft.needs_from_wgo, 1))
                {
                    // If the player's ingredients are all present too, the only thing missing is fuel.
                    var inv = MainGame.me?.player?.GetMultiInventoryForInteraction();
                    bool materialsOk = inv == null
                        || inv.IsEnoughItems(craft.needs, MultiInventory.DestinationType.AllFromFirst, null, 1);
                    if (materialsOk) return "No fuel";
                }
            }
        }
        catch { }
        return "Not enough materials";
    }

    /// <summary>Comma-separated "amount item" list of a recipe's ingredients, or null.</summary>
    private static string CraftNeedsText(CraftItemGUI cri)
    {
        try { return CraftDefNeedsText(cri?.current_craft); }
        catch { return null; }
    }

    /// <summary>Comma-separated "amount item" list of a craft definition's needs, or null.</summary>
    private static string CraftDefNeedsText(CraftDefinition craft)
    {
        try
        {
            if (craft?.needs == null || craft.needs.Count == 0) return null;

            var parts = new List<string>();
            foreach (var need in craft.needs)
            {
                if (need == null) continue;
                var amt = need.value;

                // Prefer the game's own localized name so we match the cells and what a sighted
                // player reads. Only when there's no readable label (some study-point icons strip
                // to nothing) fall back to a spelled-out resource name, so a cost is never silently dropped.
                var iname = ScreenReader.StripNguiCodes(need.definition?.GetItemName() ?? "")?.Trim();
                Plugin.Log?.LogInfo($"[CRAFT NEED] id='{need.id}', definition={need.definition != null}, localized='{iname}'");
                if (string.IsNullOrWhiteSpace(iname)) iname = SpecialNeedName(need.id) ?? need.id;
                Plugin.Log?.LogInfo($"[CRAFT NEED] Final: id='{need.id}' -> label='{iname}'");
                if (string.IsNullOrWhiteSpace(iname)) continue;
                parts.Add(amt > 1 ? $"{amt} {iname}" : iname);
            }
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Fallback spoken name for a craft "need" that is a resource pool rather than an inventory
    /// item — faith and the study-point colours (r/g/b/v/gratitude) — used only when the game has
    /// no readable label for it (a few icons strip to nothing). The localized name ("Glaube",
    /// "Wissenschaft", …) is preferred when present. Returns null for a normal item.
    /// </summary>
    private static string SpecialNeedName(string id)
    {
        switch (id)
        {
            case "faith": return "faith";
            case "r": return "red points";
            case "g": return "green points";
            case "b": return "blue points";
            case "v": return "violet points";
            case "gratitude_points": return "gratitude points";
            default: return null;
        }
    }

    private static readonly System.Reflection.FieldInfo _crafteryWgoField =
        AccessTools.Field(typeof(BaseCraftGUI), "craftery_wgo");

    /// <summary>
    /// Whether an alchemy workbench is currently open (its obj_id carries the "alchemy" tag, e.g.
    /// mf_alchemy_*). Used to gate the study-reward read-out to the one place it's useful — the
    /// table where you actually study — so it doesn't narrate over every inventory and chest. The
    /// station window stays shown underneath its resource picker, so iterating the shown GUIs finds
    /// it even while the item-pick grid is on top.
    /// </summary>
    internal static bool IsAlchemyStationOpen()
    {
        try
        {
            if (!GUIElements.me) return false;
            foreach (var g in GUIElements.me.GetComponentsInChildren<BaseGUI>(true))
            {
                if (!g.is_shown || !(g is BaseCraftGUI bcg)) continue;
                var wgo = _crafteryWgoField?.GetValue(bcg) as WorldGameObject;
                if (wgo?.obj_id != null && wgo.obj_id.Contains("alchemy")) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Describe why a craft window opened with no recipes, so a blind player hears more than
    /// silence. The common case is a broken object (e.g. beegarden_table_broken — the broken
    /// beehive) whose repair craft the game hides until its technology is unlocked: a sighted
    /// player sees a locked/empty window, but we get nothing to read. We reach past the GUI's
    /// filtered list to the station's full craft list (which still holds the locked repair) and
    /// report whether it's a repair, whether it's locked, and the materials it will consume.
    /// </summary>
    private static string EmptyCraftWindowInfo(CraftGUI craftGui)
    {
        try
        {
            var wgo = _crafteryWgoField?.GetValue(craftGui) as WorldGameObject;
            if (wgo == null) return null;

            var objName = InteractionDetector.LocalizedObjectName(wgo.obj_id);
            var crafts = (wgo.obj_def != null && wgo.obj_def.has_craft) ? wgo.components?.craft?.crafts : null;
            if (crafts == null || crafts.Count == 0)
                return $"{objName}. Nothing to make here";

            // A craft that swaps the object for another (change_wgo) is its repair/upgrade.
            CraftDefinition repair = null;
            bool anyLocked = false;
            foreach (var c in crafts)
            {
                if (c == null) continue;
                if (c.IsLocked()) anyLocked = true;
                if (repair == null && !string.IsNullOrEmpty(c.change_wgo)) repair = c;
            }

            if (repair != null)
            {
                var needs = CraftDefNeedsText(repair);
                var msg = repair.IsLocked()
                    ? $"{objName}. Repair is locked, research it in the technology tree first"
                    : $"{objName}. Repairable";
                if (!string.IsNullOrEmpty(needs)) msg += $". Repair needs {needs}";
                return msg;
            }

            return anyLocked
                ? $"{objName}. No recipes available yet, research them in the technology tree"
                : $"{objName}. Nothing to make here";
        }
        catch
        {
            return null;
        }
    }

    // --- Technology tree --------------------------------------------------------------------

    /// <summary>
    /// List the tech tree as navigable rows: first one "Category" row per visible branch (Enter
    /// switches branch), then one row per tech in the currently selected branch. Each tech row
    /// reads its name, what it unlocks, cost and state; Enter buys an available tech (which opens
    /// the game's own confirm dialog, announced by the normal flow).
    /// </summary>
    private static void DiscoverTechTree(TechTreeGUI techTree)
    {
        int branch = techTree.current_branch;

        // Branch tabs. Mirror TechTreeGUI.Draw: branch ids 0.._maxBranch, kept if the save says
        // the branch is visible. Each shows its localized name; the active one is marked.
        int maxBranch = 0;
        try
        {
            foreach (var t in GameBalance.me.techs_data)
                if (t.branch_type > maxBranch) maxBranch = t.branch_type;
        }
        catch { }

        for (int j = 0; j <= maxBranch; j++)
        {
            bool visible;
            try { visible = MainGame.me.save.IsTechBranchVisible(j); }
            catch { visible = false; }
            if (!visible) continue;

            int captured = j;
            var name = TechBranchName(j);
            var label = j == branch ? $"Category: {name}, current" : $"Category: {name}";
            Elements.Add(new GUIElement
            {
                Go = techTree.gameObject,
                Label = label,
                Type = ElementType.Button,
                OnActivate = () => SwitchTechBranch(techTree, captured)
            });
        }

        // Techs in the current branch (everything the game would draw — i.e. not Invisible),
        // read in a stable order: by column (prerequisite depth), then row.
        var techs = new List<TechDefinition>();
        try
        {
            foreach (var t in GameBalance.me.techs_data)
            {
                if (t.branch_type != branch) continue;
                if (t.GetState() == TechDefinition.TechState.Invisible) continue;
                techs.Add(t);
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[TECH] enumerate failed: {ex.Message}"); }

        techs.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));

        foreach (var tech in techs)
        {
            var captured = tech;
            Elements.Add(new GUIElement
            {
                Go = techTree.gameObject,
                Label = TechLabel(tech),
                Type = ElementType.Button,
                OnActivate = () => ClickTech(techTree, captured)
            });
        }

        Plugin.Log.LogInfo($"[TECH] branch {branch}: {techs.Count} tech(s)");
    }

    /// <summary>Localized name of a tech branch (the "tbranch_N" token), or a fallback.</summary>
    private static string TechBranchName(int branchId)
    {
        try
        {
            var loc = ScreenReader.StripNguiCodes(GJL.L("tbranch_" + branchId) ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(loc) && !loc.Contains("!")) return loc;
        }
        catch { }
        return $"Branch {branchId}";
    }

    /// <summary>
    /// Spoken label for one tech: name, what it unlocks, its cost in tech points, and its state
    /// (unlocked / available / locked / not enough points / hidden).
    /// </summary>
    private static string TechLabel(TechDefinition tech)
    {
        TechDefinition.TechState state;
        try { state = tech.GetState(); }
        catch { state = TechDefinition.TechState.Unavailable; }

        // Hidden techs show only as a question mark in-game — don't leak their contents.
        if (state == TechDefinition.TechState.Hidden)
            return "Locked technology, not yet revealed";

        var parts = new List<string>();

        var name = ScreenReader.StripNguiCodes(GJL.L(tech.id) ?? tech.id)?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Contains("!")) name = tech.id;
        parts.Add(name);

        var unlocks = TechUnlocksText(tech);
        if (!string.IsNullOrEmpty(unlocks)) parts.Add($"Unlocks {unlocks}");

        var cost = TechPriceText(tech);

        switch (state)
        {
            case TechDefinition.TechState.Purchased:
                parts.Add("already unlocked");
                break;
            case TechDefinition.TechState.AvailableForPurchase:
                parts.Add(string.IsNullOrEmpty(cost) ? "available" : $"available, costs {cost}");
                break;
            default: // Unavailable
                bool affordable = false;
                try { affordable = MainGame.me.player.IsEnough(tech.price); }
                catch { }
                if (!affordable && !string.IsNullOrEmpty(cost))
                    parts.Add($"locked, costs {cost}, not enough points");
                else
                    parts.Add("locked, requires earlier technologies");
                break;
        }

        return string.Join(". ", parts);
    }

    /// <summary>
    /// What a tech unlocks (recipes, perks, gathering, …): each unlock's name, followed by its
    /// description when the game has one. Perks in particular carry a "what it does" description
    /// (the on-screen tooltip text) that is meaningless without sight — include it so the player
    /// hears the effect, not just the perk's name.
    /// </summary>
    private static string TechUnlocksText(TechDefinition tech)
    {
        try
        {
            var list = tech.GetVisibleUnlocksList();
            if (list == null || list.Count == 0) return null;

            var parts = new List<string>();
            foreach (var u in list)
            {
                if (u == null) continue;
                var data = u.GetData();
                if (data == null) continue;

                var n = ScreenReader.StripNguiCodes(data.name ?? "")?.Trim();
                if (string.IsNullOrWhiteSpace(n) || n.Contains("!")) continue;

                var desc = ScreenReader.StripNguiCodes(data.description ?? "")?.Trim();
                if (!string.IsNullOrWhiteSpace(desc) && !desc.Contains("!"))
                    parts.Add($"{n}: {desc}");
                else
                    parts.Add(n);
            }
            return parts.Count > 0 ? string.Join("; ", parts) : null;
        }
        catch { return null; }
    }

    /// <summary>A tech's cost spoken as tech points, e.g. "2 red, 1 green".</summary>
    private static string TechPriceText(TechDefinition tech)
    {
        try
        {
            var price = tech.price;
            if (price == null || price.IsEmpty()) return null;

            var parts = new List<string>();
            foreach (var type in price.Types)
            {
                var v = Mathf.RoundToInt(price.Get(type));
                if (v <= 0) continue;
                parts.Add($"{v} {TechPointName(type)}");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
        catch { return null; }
    }

    // The three tech-point colours (and the rarer ones) spoken as words. These are the (r)/(g)/(b)
    // coin-style sprites the on-screen cost label uses, which don't convert to speech.
    private static string TechPointName(string type)
    {
        switch (type)
        {
            case "r": return "red points";
            case "g": return "green points";
            case "b": return "blue points";
            case "v": return "violet points";
            case "gratitude_points": return "gratitude points";
            default: return type;
        }
    }

    /// <summary>Switch the tech tree to a branch, then re-list and announce its techs.</summary>
    private static void SwitchTechBranch(TechTreeGUI techTree, int branchId)
    {
        try { techTree.SelectTechBranch(branchId); }
        catch (Exception ex) { Plugin.Log.LogWarning($"[TECH] branch switch failed: {ex.Message}"); }

        Elements.Clear();
        DiscoverElements(techTree);

        var active = GetActiveElements();
        var name = TechBranchName(branchId);

        // Land on the first tech in the branch (skip past the category rows) so the player hears
        // the branch's contents straight away.
        var idx = active.FindIndex(e => e.OnActivate != null && !e.Label.StartsWith("Category:"));
        if (idx < 0) idx = active.Count > 0 ? 0 : -1;
        SelectedIndex = idx;

        if (idx >= 0)
            ScreenReader.Say($"{name}. {active[idx].ReadLabel()}");
        else
            ScreenReader.Say($"{name}. Empty");
    }

    /// <summary>
    /// Click a tech, exactly as the mouse would. An available tech opens the game's buy-confirm
    /// dialog; a hidden / un-unlockable tech opens an info dialog — both are picked up and
    /// announced by CheckForNewGUI. Only when no dialog opens (e.g. an already-purchased tech)
    /// do we re-read the row in place so the press still gives feedback.
    /// </summary>
    private static void ClickTech(TechTreeGUI techTree, TechDefinition tech)
    {
        try { techTree.OnClickTech(tech); }
        catch (Exception ex) { Plugin.Log.LogWarning($"[TECH] click failed: {ex.Message}"); }

        if (TechDialogOpen()) return;

        RefreshCurrentGUI(SelectedIndex);
    }

    // True if the tech buy-confirm dialog or a generic OK dialog is currently shown (so the
    // tech click opened a modal that the normal GUI flow will announce next).
    private static bool TechDialogOpen()
    {
        try
        {
            if (!GUIElements.me) return false;
            if (GUIElements.me.tech_dialog != null && GUIElements.me.tech_dialog.is_shown) return true;
            if (GUIElements.me.dialog != null && GUIElements.me.dialog.is_shown) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Build the navigable element list for the vendor (trade) screen: every item cell
    /// (vendor stock, the player's inventory, and both offer widgets — labelled Buy/Sell/
    /// Your offer/Vendor offer by InventoryItemHandler), followed by clearly-named
    /// "Confirm trade" and "Cancel offer" buttons. Activating an item moves it into/out of
    /// an offer; activating Confirm accepts the assembled offer.
    /// </summary>
    // GraveGUI keeps the grave object and its parts in private fields, and routes a part click
    // through the private OnGravePartPressed(UIButton, Item, ItemDefinition.ItemType). We read the
    // fields and invoke that method so Enter does exactly what a mouse click does.
    private static readonly System.Reflection.FieldInfo _graveWgoField =
        AccessTools.Field(typeof(GraveGUI), "_grave_wgo");
    private static readonly System.Reflection.FieldInfo _graveFenceField =
        AccessTools.Field(typeof(GraveGUI), "_fence");
    private static readonly System.Reflection.FieldInfo _graveCrossField =
        AccessTools.Field(typeof(GraveGUI), "_cross");
    private static readonly System.Reflection.MethodInfo _gravePartPressedMethod =
        AccessTools.Method(typeof(GraveGUI), "OnGravePartPressed");

    // List the grave's repairable parts (fence first — that's the worn-fence/repair-kit case the
    // player came for — then cross). Each is a navigable row reading the part's condition; Enter
    // opens its repair/replace craft. The body (exhumation) is left to the dedicated dig flow.
    private static void DiscoverGraveParts(GraveGUI grave)
    {
        AddGravePartElement(grave, "Fence", _graveFenceField, ItemDefinition.ItemType.GraveFence);
        AddGravePartElement(grave, "Cross", _graveCrossField, ItemDefinition.ItemType.GraveStone);
        Plugin.Log.LogInfo($"[GRAVE] Discovered {Elements.Count} grave part element(s)");
    }

    private static void AddGravePartElement(GraveGUI grave, string name,
        System.Reflection.FieldInfo field, ItemDefinition.ItemType type)
    {
        Elements.Add(new GUIElement
        {
            Go = grave.gameObject,
            Label = GravePartLabel(grave, name, field),
            Type = ElementType.Button,
            // Re-read the part each time so the condition stays current after a redraw.
            ReadDynamic = () => GravePartLabel(grave, name, field),
            OnActivate = () => ActivateGravePart(grave, field, type)
        });
    }

    // "Fence: wooden fence, condition 42 percent. Press Enter to repair", or "No fence, press
    // Enter to add" when the slot is empty. Condition is the part item's durability (0..1).
    private static string GravePartLabel(GraveGUI grave, string name, System.Reflection.FieldInfo field)
    {
        try
        {
            var item = field?.GetValue(grave) as Item;
            if (item == null || item.IsEmpty())
                return $"No {name.ToLowerInvariant()}, press Enter to add";

            int pct = Mathf.RoundToInt(Mathf.Clamp01(item.durability) * 100f);
            var itemName = ScreenReader.StripNguiCodes(item.definition?.GetItemName() ?? name)?.Trim();
            if (string.IsNullOrWhiteSpace(itemName)) itemName = name;
            return $"{name}: {itemName}, condition {pct} percent. Press Enter to repair";
        }
        catch
        {
            return name;
        }
    }

    // Mirror a click on the grave-part cell: the game opens the fix/replace craft for a present
    // part (CraftGUI via OpenAsGrave) or the resource picker to add a missing one. Re-read the
    // field at activation time so we pass the current item.
    private static void ActivateGravePart(GraveGUI grave, System.Reflection.FieldInfo field,
        ItemDefinition.ItemType type)
    {
        try
        {
            var item = field?.GetValue(grave) as Item;
            _gravePartPressedMethod?.Invoke(grave, new object[] { null, item, type });
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[GRAVE] grave-part activate failed: {ex.Message}");
        }
    }

    private static void DiscoverVendor(VendorGUI vendor)
    {
        InventoryItemHandler.DiscoverItemCells(vendor, Elements);

        // Drive the trade actions directly rather than via the buttons' anonymous
        // btn/spr children: FinishOffer accepts the assembled deal, ResetOrderAndRedraw
        // returns the offered items. The buttons' own onClick wiring proved unreliable
        // through our SendMessage path, and these public methods are unambiguous.
        AddVendorButton(vendor.btn_confirm, "Confirm trade", () => ConfirmVendorTrade(vendor));
        AddVendorButton(vendor.btn_cancel, "Cancel offer", () => CancelVendorOffer(vendor));

        // The vendor's purse is shown on-screen but never voices. Put it first so it's spoken
        // when the trade screen opens (the open flow auto-reads the first row) and is reachable
        // with the arrow keys at any time. The label is dynamic so it reflects the live value —
        // selling to the vendor draws down their money. Enter just re-reads it.
        AddVendorMoneyRow(vendor);

        Plugin.Log.LogInfo($"[VENDOR] Discovered {Elements.Count} element(s)");
    }

    // Add a read-only "Vendor money" row at the front of the vendor element list.
    private static void AddVendorMoneyRow(VendorGUI vendor)
    {
        if (vendor?.trading?.trader == null) return;
        if (Elements.Any(e => e.Go == vendor.gameObject)) return;

        Elements.Insert(0, new GUIElement
        {
            Go = vendor.gameObject,
            Type = ElementType.Button,
            Label = "Vendor money",
            ReadDynamic = () => DescribeVendorMoney(vendor),
            // No real action — re-speak the live value rather than fall through to the generic
            // button SendMessage path (which would poke the vendor GUI's own components).
            OnActivate = () => ScreenReader.Say(DescribeVendorMoney(vendor))
        });
    }

    // How much money the vendor currently has to spend, spoken ("Vendor has 3 gold, 20 silver").
    internal static string DescribeVendorMoney(VendorGUI vendor)
    {
        try
        {
            var trader = vendor?.trading?.trader;
            if (trader == null) return "Vendor money unknown";
            return $"Vendor has {MoneyToSpeech(trader.cur_money)}";
        }
        catch
        {
            return "Vendor money unknown";
        }
    }

    private static void AddVendorButton(UIButton button, string label, Action onActivate)
    {
        if (button == null) return;
        if (Elements.Any(e => e.Go == button.gameObject)) return;

        Elements.Add(new GUIElement
        {
            Go = button.gameObject,
            Label = label,
            Type = ElementType.Button,
            OnActivate = onActivate
        });
    }

    // Accept the assembled offer. All announcements (reject reason, "No offer to confirm",
    // "Trade complete") and the post-trade re-discovery are handled by the FinishOffer Harmony
    // patch (Patches.VendorGUI_FinishOffer_*), so the game's own confirm key gets the same
    // feedback as our nav button. Here we just trigger it.
    private static void ConfirmVendorTrade(VendorGUI vendor)
    {
        try { vendor.FinishOffer(); }
        catch (Exception ex) { Plugin.Log.LogWarning($"[VENDOR] confirm failed: {ex.Message}"); }
    }

    // Why CanAcceptOffer rejected the assembled deal, mirroring its four checks in
    // Trading.CanAcceptOffer. Falls back to the game's own localized message.
    internal static string VendorRejectReason(Trading trading)
    {
        try
        {
            if (!trading.player_inventory.CanAddItems(trading.trader.cur_offer.inventory, include_bags: true))
                return "Your inventory is full";
            if (!trading.trader.inventory.CanAddItems(trading.player_offer.inventory))
                return "The vendor can't carry these items";
            float balance = trading.GetTotalBalance();
            if (trading.player_money + balance < 0f)
                return "You don't have enough money";
            if (trading.trader.cur_money - balance < 0f)
                return "The vendor doesn't have enough money";
        }
        catch { }

        var loc = ScreenReader.StripNguiCodes(GJL.L("cant_accept_offer") ?? "").Trim();
        return string.IsNullOrEmpty(loc) ? "Trade not possible" : loc;
    }

    // Return all offered items to their owners and re-announce the cleared state.
    private static void CancelVendorOffer(VendorGUI vendor)
    {
        try
        {
            vendor.ResetOrderAndRedraw();
            AnnounceVendorState(vendor, "Offer cancelled");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[VENDOR] cancel failed: {ex.Message}");
        }
    }

    // Re-discover the vendor screen after a confirm/cancel mutated it, focus the first row,
    // and speak the given prefix followed by that row.
    internal static void AnnounceVendorState(VendorGUI vendor, string prefix)
    {
        Elements.Clear();
        DiscoverElements(vendor);

        var active = GetActiveElements();
        if (active.Count == 0)
        {
            SelectedIndex = -1;
            ScreenReader.Say(prefix);
            return;
        }

        SelectedIndex = 0;
        ScreenReader.Say($"{prefix}. {active[0].ReadLabel()}");
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

    // Re-discover the current GUI's elements in place (used after a chest move or an inventory
    // use mutates the grids) and keep focus near where it was, re-announcing the now-current row.
    // An optional prefix (e.g. "Used teleport stone") leads the announcement so a single,
    // uninterrupted Say carries both what happened and where focus landed.
    private static void RefreshCurrentGUI(int focusIndex, string prefix = null)
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
            ScreenReader.Say(Join(prefix, string.IsNullOrEmpty(emptyDesc) ? "Empty" : emptyDesc));
            return;
        }

        SelectedIndex = Mathf.Clamp(focusIndex, 0, active.Count - 1);
        var label = active[SelectedIndex].ReadLabel();
        ScreenReader.Say(Join(prefix, string.IsNullOrEmpty(emptyDesc) ? label : $"{emptyDesc}. {label}"));
    }

    // Join an optional lead-in (e.g. "Used teleport stone") to the main announcement.
    private static string Join(string prefix, string body)
    {
        if (string.IsNullOrEmpty(prefix)) return body;
        if (string.IsNullOrEmpty(body)) return prefix;
        return $"{prefix}. {body}";
    }

    // True if a count/price picker (ItemCountGUI) is currently shown. Used right after pressing
    // a vendor item: a stack opens the picker instead of moving, and _currentGUI hasn't updated
    // yet (CheckForNewGUI runs next frame), so we'd otherwise announce a stale "Even trade".
    private static bool CountPickerOpen()
    {
        try
        {
            if (!GUIElements.me) return false;
            var picker = GUIElements.me.GetComponentInChildren<ItemCountGUI>(true);
            return picker != null && picker.is_shown;
        }
        catch
        {
            return false;
        }
    }

    // Confirm the open amount/price picker by invoking its private OnConfirm, which fires the
    // game's own confirm delegate with the current slider value (moving the chosen amount into
    // the vendor offer / chest) and then hides the picker. Done via reflection because the
    // keyboard has no binding for GameKey.Select, so the native confirm path never runs for us.
    private static System.Reflection.MethodInfo _itemCountConfirm;
    private static void ConfirmCountPicker(ItemCountGUI picker)
    {
        try
        {
            _itemCountConfirm ??= AccessTools.Method(typeof(ItemCountGUI), "OnConfirm");
            _itemCountConfirm?.Invoke(picker, null);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[VENDOR] count picker confirm failed: {ex.Message}");
        }
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
    internal static string MoneyToSpeech(float value)
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
        // The amount/price picker (ItemCountGUI) confirms through its DialogButtonsGUI "ok"
        // button, which only responds to a mouse click or the gamepad Select key. GameKey.Select
        // has NO keyboard binding (see KeyBindings), so a keyboard player can't confirm it
        // natively, and pressing a "discovered" button is unreliable (the slider's own
        // inc/dec buttons get discovered too, so index 0 isn't necessarily "ok"). That's why a
        // chosen stack never moved into the vendor offer. Invoke the picker's own confirm
        // directly so Enter always applies the chosen amount; the SetOnHide callback redraws the
        // vendor and CheckForNewGUI (called right after) re-announces the trade screen.
        if (_currentGUI is ItemCountGUI picker)
        {
            ConfirmCountPicker(picker);
            return;
        }

        var active = GetActiveElements();
        if (SelectedIndex < 0 || SelectedIndex >= active.Count) return;

        var elem = active[SelectedIndex];

        // Elements with an explicit action (vendor Confirm/Cancel) own their behaviour,
        // including any announcement and re-discovery, so run it and stop here.
        if (elem.OnActivate != null)
        {
            elem.OnActivate();
            return;
        }

        if (elem.Type == ElementType.ItemCell)
        {
            var prevIndex = SelectedIndex;

            // The player's own inventory: pick the item's primary action (use/equip/open bag)
            // rather than the generic left-click press, which does nothing for usable items like
            // the teleport stone. Re-announce and refresh afterwards (using consumes a stack), but
            // skip the refresh when the item closed the inventory (e.g. teleport opens the map).
            if (_currentGUI is InventoryGUI)
            {
                var (summary, closed) = InventoryItemHandler.ActivateInventoryItem(elem.Cell);
                if (closed || !(_currentGUI is InventoryGUI))
                {
                    // The item closed the inventory (e.g. teleport opens the map). Just speak the
                    // summary; CheckForNewGUI will announce whatever GUI opens next.
                    if (!string.IsNullOrEmpty(summary)) ScreenReader.Say(summary);
                }
                else
                {
                    // Re-discover the (possibly mutated) grid and lead the announcement with the
                    // summary so it isn't interrupted by the refreshed row.
                    RefreshCurrentGUI(prevIndex, summary);
                }
                return;
            }

            // A greyed vendor cell can't be moved into an offer (tier-locked buy item, or an
            // item the vendor won't buy) — the game disables its press, so PressItemCell would
            // no-op and RefreshVendorAfterMove would then misread the unchanged balance as "Even
            // trade". Say why instead and stop.
            if (_currentGUI is VendorGUI && elem.Cell != null && elem.Cell.is_inactive_state)
            {
                ScreenReader.Say("Not available to trade");
                return;
            }

            // A crafting station (furnace, etc.) closes its window after some crafts — adding
            // fuel, or a single-shot craft. The hidden window has no active rows, so the refresh
            // below would re-read it as "Nothing to make here". Capture what we're about to make
            // so we can instead announce the outcome ("Brennstoff crafted" / "Started crafting …").
            CraftGUI craftStation = null;
            string pressedRecipeName = null;
            CraftDefinition pressedCraftDef = null;
            if (_currentGUI is CraftGUI cg && !(MainGame.me?.build_mode_logics?.IsBuilding() == true))
            {
                craftStation = cg;
                try
                {
                    var cri = elem.Cell?.GetComponentInParent<CraftItemGUI>();
                    pressedRecipeName = RecipeDisplayName(cri);
                    pressedCraftDef = cri?.current_craft;
                }
                catch { }
            }

            // Press the item cell, which fires its on-action callback (e.g. the autopsy
            // table's "extract this body part" flow → confirm dialog the mod reads next).
            InventoryItemHandler.PressItemCell(elem.Cell);

            // A chest moves the item and redraws both grids in place — same GUI, but our
            // cached cell list is now stale. Re-discover so the player isn't navigating
            // ghost cells, and keep them on a sensible row. (Stackable items instead open a
            // count picker, a new GUI that the normal flow will announce.)
            if (_currentGUI is ChestGUI)
                RefreshCurrentGUI(prevIndex);
            else if (_currentGUI is VendorGUI vendor)
            {
                // A stackable item opens a count/price picker (a new GUI the normal flow
                // announces) instead of moving immediately. Don't speak a premature "Even
                // trade" balance over it — nothing has moved yet, so the balance is still zero.
                if (!CountPickerOpen())
                    RefreshVendorAfterMove(vendor, prevIndex);
            }
            else if (craftStation != null)
            {
                // If the craft closed the window (furnace fuel, single-shot crafts) there's
                // nothing left to read — announce the result. Otherwise the station stays open,
                // so re-read the row: crafting consumes ingredients, and the player hears whether
                // they can still make another (". Ready" -> ". Not enough materials").
                if (!craftStation.is_shown)
                    ScreenReader.Say(CraftStartedMessage(craftStation, pressedCraftDef, pressedRecipeName), interrupt: true);
                else
                    RefreshCurrentGUI(prevIndex);
            }
            return;
        }

        if (elem.Type == ElementType.Button)
        {
            // Save-slot rows: Enter loads the save (or starts a new game for the first row),
            // exactly as clicking the slot does. Deletion is handled separately by the Delete key.
            if (elem.SaveSlot != null)
            {
                // Loading a save / starting a new game makes the title screen flash back up during
                // the transition; flag it so it announces "Loading" instead of "Title Screen".
                TitleScreenAccessibility.LoadingStarted = true;
                elem.SaveSlot.OnSlotSelect();
                return;
            }

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

    // Delete the currently selected save slot. Only meaningful on the save-slots screen; the
    // game opens its own yes/no confirmation dialog, which the mod reads like any other dialog.
    internal static void DeleteSelected()
    {
        var active = GetActiveElements();
        if (SelectedIndex < 0 || SelectedIndex >= active.Count) return;

        var elem = active[SelectedIndex];
        if (elem.SaveSlot != null)
            elem.SaveSlot.OnDeletePressed();
    }

    internal static void AdjustLeft()
    {
        var active = GetActiveElements();
        if (SelectedIndex < 0 || SelectedIndex >= active.Count) return;

        var elem = active[SelectedIndex];
        if (elem.OnAdjustLeft != null)
        {
            elem.OnAdjustLeft();
        }
        else if (elem.Type == ElementType.Switcher && elem.OptionsSwitcher != null)
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
        if (elem.OnAdjustRight != null)
        {
            elem.OnAdjustRight();
        }
        else if (elem.Type == ElementType.Switcher && elem.OptionsSwitcher != null)
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

        // The raw iteration above just takes the last is_shown window in hierarchy order,
        // which can land on a window still showing *underneath* another — e.g. a chest left
        // open beneath the inventory. That makes us keep reading the wrong grid's item cells
        // (the reported "inventory still shows the chest's contents" bug). BaseGUI.active_gui
        // is the game's authoritative top-of-stack window, so prefer it when it's a real,
        // shown, non-HUD window.
        var activeGui = BaseGUI.active_gui;
        if (activeGui != null && activeGui.is_shown)
            topGUI = activeGui;

        if (topGUI == _currentGUI) return;

        if (_currentGUI != null)
            OnGUIClosed(_currentGUI);

        if (topGUI != null)
            OnGUIOpened(topGUI);
    }
}
