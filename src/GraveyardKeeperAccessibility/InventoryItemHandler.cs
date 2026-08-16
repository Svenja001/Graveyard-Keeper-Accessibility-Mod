namespace GraveyardKeeperAccessibility;

internal static class InventoryItemHandler
{
    private static ManualLogSource _log;
    private static BaseGUI _currentInventoryGUI;

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
            // Only remember the GUI so we can say "Inventory closed" later and detect empty
            // panels. We deliberately do NOT scrape every UILabel here: for the player's own
            // InventoryGUI those labels include the shared HUD (health/energy, money), the
            // buffs panel ("Keine aktiven Buffs") and the quest log, which were being read out
            // as junk ("Items: 10, 100, 100, ...") before the real announcement. The actual
            // item list is read by the item-cell navigation in MainMenuPatches.OnGUIOpened.
            _currentInventoryGUI = gui;
        }
    }

    internal static void OnGUIClosed(BaseGUI gui)
    {
        if (gui == _currentInventoryGUI)
        {
            // Only the player's own inventory gets a spoken close; chests/containers close
            // silently as before. (We say this here rather than scraping labels — see below.)
            if (_currentInventoryGUI is InventoryGUI)
                ScreenReader.Say(Loc.Get("inventory.closed"));

            _currentInventoryGUI = null;
        }
    }

    private static bool IsInventoryGUI(string guiTypeName)
    {
        return guiTypeName.Contains("Inventory") ||
               guiTypeName.Contains("Chest") ||
               guiTypeName.Contains("Storage") ||
               guiTypeName.Contains("Container") ||
               guiTypeName.Contains("Bag");
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

            // At the autopsy/dissection table the part cells aren't items to keep — they're what
            // you cut out of the corpse (subtracts their skulls). And when the shared resource
            // picker is open ON TOP of an autopsy table, it can only be the part-insertion list
            // (adds their skulls — AutopsyGUI opens the picker for nothing else). In both cases
            // tell DescribeItemCell to voice the body-effect, not the part's bare value.
            BodyPartView partView = gui is AutopsyGUI
                ? BodyPartView.Extract
                : (gui is CraftResourcesSelectGUI && IsAutopsyTableOpen())
                    ? BodyPartView.Insert
                    : BodyPartView.Value;

            // An opened bag's contents live in InventoryGUI.bag_panel. Include its cells even if
            // that panel isn't parented under the window itself, or the bag would look empty.
            var cells = gui.GetComponentsInChildren<BaseItemCellGUI>(true).ToList();
            cells.AddRange(BagHandler.ExtraBagPanelCells(gui));

            // While a bag is open, only its side panel represents it; the same bag's inline row in
            // the main grid is greyed out and inert, so listing it too would be a dead duplicate.
            var invGui = gui as InventoryGUI;
            var openBag = invGui != null ? BagHandler.OpenBagIn(invGui) : null;

            foreach (var cell in cells)
            {
                if (cell == null || !cell.gameObject.activeInHierarchy) continue;
                // Only list cells that actually belong to this GUI. A just-closed chest's cells
                // can linger a frame (Unity defers Destroy) or get caught by a stale current-GUI;
                // without this guard they'd surface in the player's plain inventory as phantoms.
                if (cell.GetComponentInParent<BaseGUI>() != gui && !BagHandler.IsBagPanelCell(cell, gui)) continue;
                if (cell.id_empty) continue;
                if (elements.Any(e => e.Go == cell.gameObject)) continue;
                if (discovered.Any(e => e.Go == cell.gameObject)) continue;
                if (openBag != null && !BagHandler.IsBagPanelCell(cell, gui)
                    && BagHandler.BagOfInlineCell(cell) == openBag) continue;

                var label = DescribeItemCell(cell, partView);
                if (string.IsNullOrEmpty(label)) continue;

                var (panel, panelName, rank) = GetPanelContext(cell, gui);
                if (!string.IsNullOrEmpty(panelName))
                    label = Loc.Fmt("inventory.panel_prefix", panelName, label);

                // Vendor cells: append each item's per-unit price so the player knows what it's
                // worth ("Sell: Bestattungsurkunde, 3, sells for 2 silver each"). Without this the
                // bare name + stack count explains nothing about the deal.
                var price = DescribeVendorPrice(cell, gui, panel);
                if (!string.IsNullOrEmpty(price))
                    label = $"{label}, {price}";

                // The study status the game prints on the item tooltip ("Studie: nicht beendet" /
                // "Alchemie-Studie: NICHT BEENDET"). Anywhere: flag the un-studied ones, since that
                // is the actionable half (carry it to the table). Only at a survey/study station do
                // we add the point payout and confirm the finished ones — that's where "already
                // studied" saves you a wasted press, and elsewhere it would be pure noise.
                var study = DescribeStudyStatus(cell.item?.definition, GUIAccessibility.IsStudyStationOpen());
                if (!string.IsNullOrEmpty(study))
                    label = $"{label}, {study}";

                // ... and once it IS studied, the tooltip's "Zerfällt in" line, which is the
                // pay-off of having studied it.
                var alchemy = DescribeAlchemyDecompose(cell.item?.definition);
                if (!string.IsNullOrEmpty(alchemy))
                    label = $"{label}, {alchemy}";

                // Greyed (inactive) cells can't be moved into an offer: on the Buy side the item
                // is tier-locked (vendor won't sell it yet), on the Sell side the vendor won't buy
                // it. The game disables the press, so without this marker the player just hears a
                // misleading "even trade" after pressing. Call it out up front, and explain *why*
                // it's locked (tier, item type, etc.) so the player knows what to do about it.
                if (gui is VendorGUI vguiLock && cell.is_inactive_state)
                {
                    var reason = VendorLockReason(cell.item?.definition, vguiLock, panel);
                    label = string.IsNullOrEmpty(reason)
                        ? Loc.Fmt("inventory.not_available", label)
                        : Loc.Fmt("inventory.not_available_reason", label, reason);
                }

                // With a bag open, every main-grid row is a candidate to put in — except the ones
                // the bag refuses (a Fishing bag takes only tackle, and so on). The game just greys
                // those cells, which says nothing out loud, so mark them as the player arrows past.
                if (openBag != null && !BagHandler.IsBagPanelCell(cell, gui)
                    && cell.item != null && !cell.item.CanBeInsertedInBag(openBag))
                    label = Loc.Fmt("inventory.does_not_fit_bag", label);

                discovered.Add(new GUIElement
                {
                    Go = cell.gameObject,
                    Label = label,
                    Type = ElementType.ItemCell,
                    Cell = cell,
                    SortRank = rank,
                    Group = panel
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
    /// True when an autopsy table's window is currently open. The dissection GUI stays shown
    /// underneath when the resource picker pops up on top of it (it gets OnAboveWindowClosed),
    /// so this lets us recognise the picker as the part-insertion list. FindObjectOfType is fine
    /// here — it runs only during the menu-open discovery pass, not per frame.
    /// </summary>
    private static bool IsAutopsyTableOpen()
    {
        try
        {
            var autopsy = UnityEngine.Object.FindObjectOfType<AutopsyGUI>();
            return autopsy != null && autopsy.is_shown;
        }
        catch { return false; }
    }

    // Stable panel identifiers. These are keys, never spoken: the side an item sits on decides how
    // it is priced and why it may be locked, so that logic must not depend on translated wording.
    // PanelDisplayName turns one into the spoken label.
    internal const string PanelChest = "chest";
    internal const string PanelInventory = "inventory";
    internal const string PanelBuy = "buy";
    internal const string PanelSell = "sell";
    internal const string PanelPlayerOffer = "player_offer";
    internal const string PanelVendorOffer = "vendor_offer";
    internal const string PanelBag = "bag";

    /// <summary>
    /// Determine which inventory panel an item cell belongs to, plus a sort rank for ordering.
    /// For a chest, the chest side (rank 0) sorts before the player side (rank 1). Other two-panel
    /// GUIs fall back to the panel's own (already localized) title, which has no stable key.
    /// </summary>
    private static (string key, string display, int rank) GetPanelContext(BaseItemCellGUI cell, BaseGUI gui)
    {
        try
        {
            if (gui is ChestGUI chest)
            {
                var chestPanel = cell.GetComponentInParent<InventoryPanelGUI>();
                if (chestPanel == chest.chest_panel) return (PanelChest, PanelDisplayName(PanelChest), 0);
                if (chestPanel == chest.player_panel) return (PanelInventory, PanelDisplayName(PanelInventory), 1);
            }

            // The vendor screen has two panels (stock you can buy, your inventory to sell)
            // plus two offer widgets (the two sides of the deal being assembled). The offer
            // widgets are bare InventoryWidgets with no InventoryPanelGUI parent, so check
            // the cell's owning widget against the vendor's offer widgets first.
            if (gui is VendorGUI vendor)
            {
                var widget = cell.GetComponentInParent<InventoryWidget>();
                if (widget != null && widget == vendor.player_offer_widget) return (PanelPlayerOffer, PanelDisplayName(PanelPlayerOffer), 2);
                if (widget != null && widget == vendor.vendor_offer_widget) return (PanelVendorOffer, PanelDisplayName(PanelVendorOffer), 3);

                var vendorPanel = cell.GetComponentInParent<InventoryPanelGUI>();
                if (vendorPanel == vendor.vendor_panel) return (PanelBuy, PanelDisplayName(PanelBuy), 0);
                if (vendorPanel == vendor.player_panel) return (PanelSell, PanelDisplayName(PanelSell), 1);
            }

            // The player's own inventory becomes a two-sided window only while a bag is actually
            // OPEN — that's when there's a side to switch to and moving things means knowing which
            // side you're on. Merely carrying a bag doesn't earn every loose row an "Inventory: "
            // prefix; the bag's own contents still say which bag they're in, since that's the part
            // you can't otherwise tell apart from the loose items.
            if (gui is InventoryGUI inv && BagHandler.HasAnyBag(inv))
            {
                if (BagHandler.IsBagPanelCell(cell, inv)) return (PanelBag, PanelDisplayName(PanelBag), 0);

                // Toolbelt / hotbar cells sit outside any inventory panel — leave them ungrouped
                // so the side switch only ever flips between the real item grids.
                if (cell.GetComponentInParent<InventoryPanelGUI>() == null) return (null, null, 3);

                var inlineBag = BagHandler.BagOfInlineCell(cell);
                if (inlineBag != null)
                    return (null, Loc.Fmt("inventory.panel.in_bag", BagHandler.BagName(inlineBag)), 2);

                return BagHandler.OpenBagIn(inv) != null
                    ? (PanelInventory, PanelDisplayName(PanelInventory), 1)
                    : (null, null, 1);
            }

            var panel = cell.GetComponentInParent<InventoryPanelGUI>();
            if (panel == null) return (null, null, 2);

            return (PanelKeyOf(panel, gui), PanelLabel(panel, gui), 2);
        }
        catch
        {
            return (null, null, 2);
        }
    }

    /// <summary>The stable key for a panel, or null when it's an unrecognised (title-named) one.</summary>
    private static string PanelKeyOf(InventoryPanelGUI panel, BaseGUI gui)
    {
        if (panel == null) return null;
        if (gui is ChestGUI chest)
        {
            if (panel == chest.chest_panel) return PanelChest;
            if (panel == chest.player_panel) return PanelInventory;
        }
        if (gui is VendorGUI vendor)
        {
            if (panel == vendor.vendor_panel) return PanelBuy;
            if (panel == vendor.player_panel) return PanelSell;
        }
        if (gui is InventoryGUI inv && panel == inv.bag_panel) return PanelBag;
        return null;
    }

    /// <summary>Spoken name for a panel key.</summary>
    private static string PanelDisplayName(string key) => Loc.Get("inventory.panel." + key);

    /// <summary>Spoken name for an inventory panel: the known sides, else the panel's own title.</summary>
    private static string PanelLabel(InventoryPanelGUI panel, BaseGUI gui)
    {
        if (panel == null) return null;
        var key = PanelKeyOf(panel, gui);
        if (key != null) return PanelDisplayName(key);
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
            var panels = gui.GetComponentsInChildren<InventoryPanelGUI>(true).ToList();
            var detachedBagPanel = BagHandler.DetachedBagPanel(gui);
            if (detachedBagPanel != null) panels.Add(detachedBagPanel);

            foreach (var panel in panels)
            {
                if (panel == null || !panel.gameObject.activeInHierarchy) continue;

                bool hasItems = panel.GetComponentsInChildren<BaseItemCellGUI>(true)
                    .Any(c => c != null && c.gameObject.activeInHierarchy && !c.id_empty);
                if (hasItems) continue;

                // An open-but-empty bag is the one case where "empty" alone isn't enough: name it
                // and say how much room it has, so the player knows what they just opened.
                if (gui is InventoryGUI inv && panel == inv.bag_panel)
                {
                    var bag = BagHandler.OpenBagIn(inv);
                    var capacity = BagHandler.DescribeCapacity(bag);
                    empties.Add(string.IsNullOrEmpty(capacity)
                        ? Loc.Fmt("inventory.panel_empty", BagHandler.BagName(bag))
                        : $"{BagHandler.BagName(bag)} {capacity}");
                    continue;
                }

                empties.Add(Loc.Fmt("inventory.panel_empty",
                    PanelLabel(panel, gui) ?? PanelDisplayName(PanelInventory)));
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
    /// <summary>How a body-part cell's skull score should be voiced, depending on the screen.</summary>
    internal enum BodyPartView
    {
        /// <summary>The part's own value (a loose part in a bag, a craft ingredient).</summary>
        Value,
        /// <summary>At the autopsy grid: the effect of cutting the part OUT of the corpse.</summary>
        Extract,
        /// <summary>At the insertion picker: the effect of putting the part INTO the corpse.</summary>
        Insert,
    }

    internal static string DescribeItemCell(BaseItemCellGUI cell, BodyPartView partView = BodyPartView.Value)
    {
        try
        {
            var item = cell.item;
            if (item == null || item.IsEmpty()) return null;

            // The autopsy grid includes a pseudo-item cell for inserting a part into the
            // body; its raw name is unreadable, so give it a clear spoken label.
            if (item.id == "insertion_button_pseudoitem")
                return Loc.Get("autopsy.insert_body_part");

            var name = ScreenReader.StripNguiCodes(item.definition?.GetItemName() ?? "").Trim();
            if (string.IsNullOrEmpty(name)) name = item.id;
            if (string.IsNullOrEmpty(name)) return null;

            // GetItemName() strips the quality suffix (e.g. "beer:3" -> "beer"), so the star tier
            // is otherwise inaudible. Append it as a spoken tier ("gold quality") for star items.
            var quality = QualityTierName(item.definition);
            if (!string.IsNullOrEmpty(quality))
                name = $"{name}, {quality}";

            // For consumables (food/potions), speak what using them gives — "gives 20 energy,
            // 4 health" — since the on-screen tooltip that shows this never voices.
            var perks = DescribeUsePerks(item.definition);
            if (!string.IsNullOrEmpty(perks))
                name = $"{name}, {perks}";

            // Tools and weapons wear out; the on-screen condition bar never voices, so a blind
            // player can't tell a fresh pickaxe from one about to snap. Speak the condition
            // percent (and a warning near the end) for any item that has durability.
            var condition = DescribeDurability(item);
            if (!string.IsNullOrEmpty(condition))
                name = $"{name}, {condition}";

            // Body parts carry their own skull score (red = bad, white = good); the on-screen
            // skull pips never voice, so speak them. At the autopsy table the value alone is
            // misleading, because extraction SUBTRACTS the part from the corpse and insertion
            // ADDS it — the opposite of each other. So in the cut-out grid we voice the removal
            // effect ("cutting out removes 1 red, loses 1 white"), in the insertion picker the
            // insertion effect ("inserting adds 3 white"), and everywhere else the bare value.
            var partSkulls = partView switch
            {
                BodyPartView.Extract => SkullInfo.DescribeRemovalEffect(item),
                BodyPartView.Insert => SkullInfo.DescribeInsertionEffect(item),
                _ => SkullInfo.DescribePart(item),
            };
            if (!string.IsNullOrEmpty(partSkulls))
                name = $"{name}, {partSkulls}";

            return item.value > 1 ? $"{name}, {item.value}" : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The item's study state, and — at the table — what studying it pays out: "not studied yet",
    /// "not studied yet, studying gives 50 blue points", or "already studied".
    ///
    /// This is the tooltip line the game draws as "Studie: beendet / nicht beendet" and, for
    /// alchemy ingredients, "Alchemie-Studie: NICHT BEENDET" — two labels, but ONE flag underneath:
    /// <c>GameSave.IsSurveyComplete</c> ignores its sub-type argument and just asks whether the
    /// one-time craft "surv:&lt;item&gt;" is done. Until it is, the item's alchemy details (what it
    /// decomposes into, which mixer slots it fits) stay hidden and its decompose recipes are absent
    /// from the alchemy bench, so "not studied yet" is the whole story either way.
    ///
    /// Not every survey pays tech points — a pure alchemy survey pays none, which is exactly why an
    /// output-only read-out stayed silent on herbs and mushrooms, the items this matters most for.
    /// So the state is reported on its own, and the payout only added when there is one.
    ///
    /// IMPORTANT: we always name a tech point by its COLOUR (PointColorName), never by the game's
    /// localized item name. The blue point ("b") is named "Wissenschaft" in the German data, which
    /// collides with the UNRELATED science resource you get from decomposing paper — so calling a
    /// blue-point reward "Wissenschaft" badly confused players. Blue points are just blue points.
    /// Returns null for items the game shows no study line for (no survey craft at all).
    /// </summary>
    private static string DescribeStudyStatus(ItemDefinition def, bool atStudyStation)
    {
        try
        {
            if (def == null) return null;
            var surveyCraft = def.GetSurveyCraft();
            if (surveyCraft == null) return null;

            if (MainGame.me?.save != null && MainGame.me.save.completed_one_time_crafts.Contains(surveyCraft.id))
                return atStudyStation ? Loc.Get("study.already") : null;

            if (surveyCraft.output == null) return Loc.Get("study.not_yet");
            if (!atStudyStation) return Loc.Get("study.not_yet");

            var parts = new List<string>();
            foreach (var outp in surveyCraft.output)
            {
                if (outp == null) continue;
                if (!TechDefinition.TECH_POINTS.Contains(outp.id)) continue;

                // The raw output .value is only a stale default. The game computes the real award by
                // evaluating each output's min_value/max_value expression — see
                // ResModificator.ProcessItemsListBeforeDrop, the same call ItemDefinition uses to
                // render the survey tooltip. Mirror that so the spoken count matches what's granted.
                // (This replaces an earlier hard "-1 for blue" hack: the "51 -> 50" gap is that
                // min_value override, which is per-item, not a universal +1 — so the -1 wrongly
                // reported 49 for items with no offset.)
                var amount = SurveyRewardAmount(outp);
                if (amount == null) continue;   // evaluates to zero => nothing actually granted

                parts.Add(PointPhrase(outp.id, amount));
            }
            return parts.Count > 0
                ? Loc.Fmt("study.not_yet_reward", string.Join(", ", parts))
                : Loc.Get("study.not_yet");
        }
        catch { return null; }
    }

    /// <summary>
    /// The whole-number tech-point amount a single survey output actually grants, spoken (e.g. "50",
    /// or "45 to 55" for a randomized range), or null when it grants nothing. Mirrors the value step
    /// of <see cref="ResModificator.ProcessItemsListBeforeDrop"/> minus the RNG: a min_value/max_value
    /// expression, when present, is authoritative and overrides the raw <c>.value</c> default (that
    /// override is the real source of the blue "51 -> 50" discrepancy); with no expression the raw
    /// value stands. Evaluated with the same (wgo = null, character = player) context the game uses.
    /// </summary>
    private static string SurveyRewardAmount(Item outp)
    {
        try
        {
            var player = MainGame.me?.player;

            if (outp.min_value != null && !outp.min_value.HasNoExpresion())
            {
                int min = Mathf.RoundToInt(outp.min_value.EvaluateFloat(null, player));
                if (min < 0) min = 0;

                int max = min;
                if (outp.max_value != null && !outp.max_value.HasNoExpresion())
                    max = Mathf.RoundToInt(outp.max_value.EvaluateFloat(null, player));
                if (max < min) max = min;   // ResModificator falls back to min when max < min

                if (max <= 0) return null;
                return min == max ? min.ToString() : Loc.Fmt("study.range", min, max);
            }

            return outp.value > 0 ? outp.value.ToString() : null;
        }
        catch
        {
            return outp.value > 0 ? outp.value.ToString() : null;
        }
    }

    /// <summary>
    /// The tooltip's "Zerfällt in" / "Decomposes into" line, spoken as "decomposes into powder,
    /// essence" — which of the three alchemy ingredient kinds this item breaks down into at the
    /// alchemy bench. The game draws it as bare icons ((alc1)(alc2), BubbleWidgetAlchemyItem
    /// DrawDecomposeInfo), which never voice, so it was silent even though it's the whole pay-off
    /// of having studied the item.
    ///
    /// Gated on the survey being done, exactly as the game gates it: the alchemy widget is only
    /// attached inside the *studied* branch of <see cref="ItemDefinition.GetItemDescription"/>,
    /// and the decompose recipes stay hidden from the bench until then
    /// (<see cref="BaseCraftGUI"/> filters AlchemyDecompose crafts on IsSurveyComplete). Speaking
    /// it early would hand out what studying is supposed to reveal.
    ///
    /// Null for alchemy ingredients themselves — <c>GetItemDetails</c> leaves <c>alchemy</c> null
    /// when <c>alch_type != None</c>, so a powder has no decompose line of its own. The companion
    /// "Alchemisch kompatible Plätze" (slots) half of that widget is dead code in this build:
    /// <c>ItemDetailsAlchemy.slots</c> is never populated, so there is nothing to read there.
    /// </summary>
    private static string DescribeAlchemyDecompose(ItemDefinition def)
    {
        try
        {
            if (def == null) return null;
            if (MainGame.me?.save == null) return null;
            // The game's own helper — it also resolves a ":quality" suffix back to the base item.
            if (!MainGame.me.save.IsSurveyComplete(CraftDefinition.CraftSubType.Alchemy, def.id)) return null;

            var decomposes = def.GetItemDetails()?.alchemy?.decomposes;
            if (decomposes == null || decomposes.Count == 0) return null;

            var kinds = new List<string>(decomposes.Count);
            foreach (var d in decomposes)
            {
                var kind = GUIAccessibility.AlchemyTypeName((ItemDefinition.AlchemyType)d);
                if (kind != null && !kinds.Contains(kind)) kinds.Add(kind);
            }
            return kinds.Count > 0 ? Loc.Fmt("alchemy.decomposes_into", string.Join(", ", kinds)) : null;
        }
        catch { return null; }
    }

    /// <summary>Fallback spoken name for a tech-point pool when no localized name is available (r/g/b/v colors or gratitude).</summary>
    private static string PointColorKey(string id)
    {
        switch (id)
        {
            case "r": return "points.red";
            case "g": return "points.green";
            case "b": return "points.blue";
            case "v": return "points.violet";
            case "gratitude_points": return "points.gratitude";
            default: return null;
        }
    }

    /// <summary>
    /// "3 grüne Punkte" / "ein grüner Punkt". <paramref name="amount"/> is a STRING because a
    /// survey reward can be a range ("45 to 55"), which is never singular.
    /// </summary>
    internal static string PointPhrase(string id, string amount)
    {
        var key = PointColorKey(id);
        if (key == null) return $"{amount} {id}";
        return Loc.Fmt(amount == "1" ? key + ".one" : key + ".other", amount);
    }

    /// <summary>
    /// Per-unit price for a vendor item cell, spoken: "costs 12 bronze" on the buy side (what
    /// the player pays) or "sells for 2 silver" on the sell side (what the vendor pays). The
    /// offer widgets are priced by which side owns them ("Your offer" = you're selling, "Vendor
    /// offer" = you're buying). Returns null for non-vendor cells or panels we don't price.
    /// Uses the game's own cost functions so the spoken price matches the on-screen coin sprites
    /// (which never voice). See <see cref="GUIAccessibility.MoneyToSpeech"/>.
    /// </summary>
    private static string DescribeVendorPrice(BaseItemCellGUI cell, BaseGUI gui, string panel)
    {
        try
        {
            if (!(gui is VendorGUI vendor) || vendor.trading == null) return null;
            if (cell?.item == null || cell.item.IsEmpty()) return null;

            bool buySide = panel == PanelBuy || panel == PanelVendorOffer;
            bool sellSide = panel == PanelSell || panel == PanelPlayerOffer;
            if (!buySide && !sellSide) return null;

            float per = buySide
                ? vendor.trading.GetSingleItemCostInTraderInventory(cell.item, 0)
                : vendor.trading.GetSingleItemCostInPlayerInventory(cell.item, 0);

            var money = GUIAccessibility.MoneyToSpeech(per);
            if (money == Loc.Get("money.nothing"))
                return sellSide ? Loc.Get("vendor.pays_nothing") : null;

            var suffix = cell.item.value > 1 ? Loc.Get("vendor.price_each") : "";
            return Loc.Fmt(buySide ? "vendor.costs" : "vendor.sells_for", money, suffix);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Why a greyed (inactive) vendor cell can't be traded, spoken as a follow-on to
    /// "not available". Mirrors the game's own eligibility checks in the same order so the
    /// reason matches why the cell is actually disabled (see Vendor.CanBuyItem / CanSellItem,
    /// both run with check_tier:true by Trading's filters). The vendor's perspective is the
    /// opposite of the panel's: the "Buy" panel is the vendor's stock it *sells* you, the
    /// "Sell" panel is your inventory it *buys* from you. Vendor tier rises automatically as
    /// you trade more goods with that vendor, so tier locks point the player at "trade more".
    /// Returns null when no specific reason applies (caller falls back to plain "not available").
    /// </summary>
    private static string VendorLockReason(ItemDefinition def, VendorGUI vendor, string panel)
    {
        try
        {
            var trader = vendor?.trading?.trader;
            if (def == null || trader == null) return null;

            // "Buy" = vendor's stock (CanSellItem / not_selling);
            // "Sell" = your inventory the vendor buys (CanBuyItem / not_buying).
            bool sellSide = panel == PanelSell;           // vendor buying from you
            var mods = sellSide ? trader.definition.not_buying : trader.definition.not_selling;

            if (def.product_types == null || def.product_types.Count == 0)
                return Loc.Get("vendor.lock.not_tradeable");

            if (def.product_tier > trader.cur_tier)
                return Loc.Fmt("vendor.lock.tier", def.product_tier, trader.cur_tier);

            // CanTradeItemType: none of the item's product types are in the vendor's list.
            bool tradesType = def.product_types.Any(t => trader.definition.GetProductTypes().Contains(t));
            if (!tradesType)
                return Loc.Get(sellSide ? "vendor.lock.kind_not_bought" : "vendor.lock.kind_not_sold");

            foreach (var m in mods)
            {
                if (m.item_name != def.id) continue;
                if (m.tier < 1)
                    return Loc.Get(sellSide ? "vendor.lock.never_buys" : "vendor.lock.never_sells");
                if (m.tier == trader.cur_tier)
                    return Loc.Get("vendor.lock.current_tier");
            }
            return null; // genuinely greyed but no check matched; fall back to "not available"
        }
        catch { return null; }
    }

    /// <summary>
    /// Spoken quality tier for an item, or null if it has no star quality. Graveyard Keeper rates
    /// craftable goods (beer, wine, food, etc.) with 1-3 stars; the game colours these bronze /
    /// silver / gold (see WorldGameObject.DropStory(bronze, silver, gold) and the ITEM_STAR_1..3
    /// tokens). Items without a star rating (quality_type == Default) return null.
    /// </summary>
    internal static string QualityTierName(ItemDefinition def)
    {
        if (def == null || def.quality_type != ItemDefinition.QualityType.Stars) return null;

        int stars = Mathf.FloorToInt(def.quality);
        switch (stars)
        {
            case 1: return Loc.Get("quality.bronze");
            case 2: return Loc.Get("quality.silver");
            case 3: return Loc.Get("quality.gold");
            case <= 0: return null;
            default: return Loc.Fmt("quality.stars", stars);
        }
    }

    /// <summary>
    /// Spoken quality qualifier for one recipe ingredient — the half of the requirement the game's
    /// own item name throws away. <c>ItemDefinition.GetItemName()</c> strips the star suffix, so a
    /// need for "cup_beer:3" reads as a plain "beer": a recipe that only accepts gold-quality beer
    /// sounded exactly like one that takes any, leaving no way to tell which of your bronze /
    /// silver / gold stacks it wants. Returns ", gold quality" for such a fixed star requirement,
    /// and ", any quality" for a multiquality group id (one with no definition of its own, e.g.
    /// "wheat" standing for wheat:1/2/3 — the craft takes whatever it finds, lowest quality first,
    /// see Item.RemoveItemNoCheck). Empty for ordinary items, and for a recipe whose window lets
    /// the player pick the quality themselves (<paramref name="playerPicksQuality"/>), where the
    /// picker announces the chosen tier instead.
    /// </summary>
    internal static string NeedQualitySuffix(Item need, bool playerPicksQuality = false)
    {
        if (need == null) return "";
        try
        {
            if (need.is_multiquality)
                return playerPicksQuality ? "" : ", " + Loc.Get("quality.any");

            var tier = QualityTierName(need.definition);
            return string.IsNullOrEmpty(tier) ? "" : $", {tier}";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Spoken summary of what using a consumable does to the player's bars — "gives 20 energy,
    /// 4 health" (or "drains 5 health" for negatives) — or null for items that can't be used or
    /// have no health/energy/sanity effect. Reads the same data the on-screen tooltip shows:
    /// <see cref="ItemDefinition.params_on_use"/> for fixed effects plus any energy/hp from
    /// <see cref="ItemDefinition.on_use_expressions"/> (foods whose value scales), mirroring
    /// ItemDefinition.GetItemDescription.
    /// </summary>
    internal static string DescribeUsePerks(ItemDefinition def)
    {
        try
        {
            if (def == null || !def.can_be_used) return null;

            float energy = def.params_on_use?.Get("energy") ?? 0f;
            float hp = def.params_on_use?.Get("hp") ?? 0f;
            float sanity = def.params_on_use?.Get("sanity") ?? 0f;

            // Foods with a scaling effect carry it in on_use_expressions, not params_on_use.
            if (def.on_use_expressions != null)
            {
                foreach (var expr in def.on_use_expressions)
                {
                    if (expr == null || expr.HasNoExpresion()) continue;
                    var parsed = GameRes.ParseSmartExpression(expr);
                    energy += parsed.Get("energy");
                    hp += parsed.Get("hp");
                }
            }

            var parts = new List<string>(3);
            AppendPerk(parts, Mathf.RoundToInt(energy), Loc.Get("perk.energy"));
            AppendPerk(parts, Mathf.RoundToInt(hp), Loc.Get("perk.health"));
            AppendPerk(parts, Mathf.RoundToInt(sanity), Loc.Get("perk.sanity"));

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Spoken condition of a tool/weapon — "condition 45%" — with a spoken warning as it nears
    /// breaking, or null for items that don't wear out (most resources, food, etc.). Mirrors the
    /// game's own <see cref="Item.durability_state"/> thresholds: below 20% is PreBroken (the
    /// on-screen bar turns red), and 0% is Broken. Durability is a 0..1 float; we round the same
    /// way the tooltip does (<see cref="Item.GetDurabilityHint"/>).
    /// </summary>
    private static string DescribeDurability(Item item)
    {
        try
        {
            if (item?.definition == null || !item.definition.has_durability) return null;

            int percent = Mathf.RoundToInt(item.durability * 100f);
            switch (item.durability_state)
            {
                case Item.DurabilityState.Broken:
                    return Loc.Get("durability.broken");
                case Item.DurabilityState.PreBroken:
                    return Loc.Fmt("durability.almost_broken", percent);
                default:
                    return Loc.Fmt("durability.condition", percent);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Compute what USING a consumable does to the player's three bars, as whole numbers:
    /// <paramref name="energy"/>, <paramref name="hp"/>, <paramref name="sanity"/> (positive =
    /// restores, negative = drains). Reads the same data as <see cref="DescribeUsePerks"/> — the
    /// fixed <see cref="ItemDefinition.params_on_use"/> plus any scaling values in
    /// <see cref="ItemDefinition.on_use_expressions"/> — so <see cref="AutoConsume"/> picks items
    /// by the very numbers the item cell speaks. Returns false (all zero) for items that can't be
    /// used at all.
    /// </summary>
    internal static bool ComputeUseEffect(ItemDefinition def, out int energy, out int hp, out int sanity)
    {
        energy = hp = sanity = 0;
        try
        {
            if (def == null || !def.can_be_used) return false;

            float e = def.params_on_use?.Get("energy") ?? 0f;
            float h = def.params_on_use?.Get("hp") ?? 0f;
            float s = def.params_on_use?.Get("sanity") ?? 0f;

            if (def.on_use_expressions != null)
            {
                foreach (var expr in def.on_use_expressions)
                {
                    if (expr == null || expr.HasNoExpresion()) continue;
                    var parsed = GameRes.ParseSmartExpression(expr);
                    e += parsed.Get("energy");
                    h += parsed.Get("hp");
                    s += parsed.Get("sanity");
                }
            }

            energy = Mathf.RoundToInt(e);
            hp = Mathf.RoundToInt(h);
            sanity = Mathf.RoundToInt(s);
            return true;
        }
        catch
        {
            energy = hp = sanity = 0;
            return false;
        }
    }

    /// <summary>Add "gives N energy" / "drains N energy" to <paramref name="parts"/> when N != 0.</summary>
    private static void AppendPerk(List<string> parts, int value, string label)
    {
        if (value > 0) parts.Add(Loc.Fmt("perk.gives", value, label));
        else if (value < 0) parts.Add(Loc.Fmt("perk.drains", -value, label));
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
    ///
    /// We fire <c>OnOver(false)</c> first, mirroring a real mouse (which always hovers before
    /// it clicks). Some GUIs cache the "currently selected" item in their hover callback rather
    /// than their press callback — VendorGUI does exactly this, so without the hover its
    /// MoveItem sees null state and silently does nothing. The hover is harmless for the chest
    /// (ChestGUI.OnItemOver returns immediately outside gamepad mode) and the autopsy/build
    /// cells, and each call is isolated so a throw in one never blocks the other.
    /// </remarks>
    internal static void PressItemCell(BaseItemCellGUI cell)
    {
        if (cell == null) return;
        try
        {
            cell.OnOver(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INVENTORY] cell OnOver threw (harmless): {ex.Message}");
        }
        try
        {
            cell.OnPressed(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INVENTORY] item cell press threw after action (harmless): {ex.Message}");
        }
    }

    /// <summary>
    /// Activate an item cell in the player's own inventory (InventoryGUI). The generic
    /// <see cref="PressItemCell"/> maps to the game's left-click, which only equips or assigns
    /// to the toolbar — it does nothing for a usable item like the teleport stone (those are
    /// "used" via the right-click context menu, not left-click). Instead pick the item's primary
    /// action the way that menu / gamepad Select would: bags open, usable items are used,
    /// equipment is equipped or unequipped.
    /// </summary>
    /// <returns>
    /// A spoken summary (null if the item had no sensible action) and whether the caller should
    /// skip its refresh — either because the inventory closed (a close-on-use item like the
    /// teleport stone hides it, so its cells are gone) or because another window took over (a
    /// bag move of a stack opens the game's amount picker, which announces itself).
    /// </returns>
    internal static (string summary, bool closedInventory) ActivateInventoryItem(BaseItemCellGUI cell, InventoryGUI gui = null)
    {
        if (cell == null) return (null, false);
        var item = cell.item;
        if (item == null || item.IsEmpty()) return (null, false);

        // Bags first: opening/closing one, and — while one is open — putting items in and taking
        // them out. That's a different action from the use/equip handling below, and it's the only
        // way to fill a bag, so it has to win before "can_be_used" eats the press (a bag full of
        // food would otherwise just get eaten instead of packed).
        gui ??= cell.GetComponentInParent<InventoryGUI>();
        if (gui != null)
        {
            var (bagSummary, skipRefresh, handled) = BagHandler.Activate(gui, cell);
            if (handled) return (bagSummary, skipRefresh);
        }

        // Register the cell as the panel's current selection, mirroring a real mouse hovering
        // before it acts. The game's inventory logic reads panel.selected_item, so this keeps
        // its state consistent with what we're about to do.
        try { cell.OnOver(false); } catch { }

        // The owning panel, so we can redraw it after using/equipping. UseItemFromInventory
        // removes the item from inventory data synchronously, but the on-screen cells keep
        // showing the old item until the panel redraws (the game's own UseItem calls Redraw()
        // right after) — without this, our caller re-discovers stale cells.
        var panel = cell.GetComponentInParent<InventoryPanelGUI>();

        var def = item.definition;
        var name = ScreenReader.StripNguiCodes(def?.GetItemName() ?? item.id)?.Trim();
        if (string.IsNullOrEmpty(name)) name = item.id;

        try
        {
            // Bags open/close; their contents become a separate panel. The cell press is the
            // game's own open/close toggle, and the caller re-discovers the cells afterwards.
            if (item.is_bag)
            {
                PressItemCell(cell);
                return (Loc.Fmt("item.opened", name), false);
            }

            // Usable items (teleport stone, food, potions): use via the game's own path, mirroring
            // InventoryGUI.UseItem — including close_inv_on_use, which hides the inventory so e.g.
            // the teleport map can open cleanly.
            if (def != null && def.can_be_used)
            {
                if (item.GetGrayedCooldownPercent() > 0)
                    return (Loc.Fmt("item.on_cooldown", name), false);

                if (def.close_inv_on_use)
                {
                    GUIElements.me.game_gui.Hide();
                    MainGame.me.player.UseItemFromInventory(item);
                    return (Loc.Fmt("item.used", name), true);
                }

                MainGame.me.player.UseItemFromInventory(item);
                try { panel?.Redraw(); } catch { }
                return (Loc.Fmt("item.used", name), false);
            }

            // Weapons / equipment: equip, or unequip if already worn.
            if (def != null && (def.IsWeapon() || def.IsEquipment()))
            {
                if (item.durability_state == Item.DurabilityState.Broken)
                    return (Loc.Fmt("item.is_broken", name), false);

                bool equipped = item.is_equipped ||
                                MainGame.me.player.data.secondary_inventory.Contains(item);
                if (equipped)
                {
                    MainGame.me.player.UnEquipItem(item);
                    try { panel?.Redraw(); } catch { }
                    return (Loc.Fmt("item.unequipped", name), false);
                }

                MainGame.me.player.EquipItem(item, -1, null);
                try { panel?.Redraw(); } catch { }
                return (Loc.Fmt("item.equipped", name), false);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INVENTORY] activate '{name}' threw: {ex.Message}");
        }

        return (null, false);
    }

    private static MethodInfo _onDestroyItem;

    /// <summary>
    /// Throw away / destroy an item from the player's own inventory — the action a sighted player
    /// reaches via the right-click context menu's "destroy" option. We drive the game's own
    /// <see cref="InventoryGUI"/>.OnDestroyItem, which checks the item is throw-out-able, then opens
    /// the localized yes/no confirm dialog (read by the mod's dialog handling) whose "yes" runs the
    /// real removal. Registering the cell as the panel's selection first (OnOver) is required: the
    /// game reads <c>panel.selected_item</c> to know what to destroy — see
    /// <see cref="ActivateInventoryItem"/>.
    /// </summary>
    /// <returns>A spoken message to say now, or null when the confirm dialog was opened (the mod
    /// reads that dialog next, so we stay silent here).</returns>
    internal static string DestroyInventoryItem(BaseItemCellGUI cell)
    {
        if (cell == null) return null;
        var item = cell.item;
        if (item == null || item.IsEmpty()) return null;

        var def = item.definition;
        var name = ScreenReader.StripNguiCodes(def?.GetItemName() ?? item.id)?.Trim();
        if (string.IsNullOrEmpty(name)) name = item.id;

        // Some items (quest items, the starting tools) are flagged un-throwable; the game greys the
        // "destroy" option out for them. Say so rather than silently doing nothing.
        if (def != null && def.player_cant_throw_out)
            return Loc.Fmt("item.cant_destroy", name);

        var invGui = cell.GetComponentInParent<InventoryGUI>();
        if (invGui == null) return null;

        // Register this cell as the panel's current selection so OnDestroyItem acts on it.
        try { cell.OnOver(false); } catch { }

        try
        {
            _onDestroyItem ??= AccessTools.Method(typeof(InventoryGUI), "OnDestroyItem");
            if (_onDestroyItem == null)
            {
                _log?.LogWarning("[INVENTORY] OnDestroyItem method not found");
                return null;
            }
            // Opens the game's "Throw away X?" yes/no dialog; the mod announces it next frame.
            _onDestroyItem.Invoke(invGui, null);
            return null;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INVENTORY] destroy '{name}' threw: {ex.Message}");
            return null;
        }
    }
}