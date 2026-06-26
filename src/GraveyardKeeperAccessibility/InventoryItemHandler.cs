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
                ScreenReader.Say("Inventory closed");

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

            foreach (var cell in gui.GetComponentsInChildren<BaseItemCellGUI>(true))
            {
                if (cell == null || !cell.gameObject.activeInHierarchy) continue;
                // Only list cells that actually belong to this GUI. A just-closed chest's cells
                // can linger a frame (Unity defers Destroy) or get caught by a stale current-GUI;
                // without this guard they'd surface in the player's plain inventory as phantoms.
                if (cell.GetComponentInParent<BaseGUI>() != gui) continue;
                if (cell.id_empty) continue;
                if (elements.Any(e => e.Go == cell.gameObject)) continue;
                if (discovered.Any(e => e.Go == cell.gameObject)) continue;

                var label = DescribeItemCell(cell);
                if (string.IsNullOrEmpty(label)) continue;

                var (panel, rank) = GetPanelContext(cell, gui);
                if (!string.IsNullOrEmpty(panel))
                    label = $"{panel}: {label}";

                // Vendor cells: append each item's per-unit price so the player knows what it's
                // worth ("Sell: Bestattungsurkunde, 3, sells for 2 silver each"). Without this the
                // bare name + stack count explains nothing about the deal.
                var price = DescribeVendorPrice(cell, gui, panel);
                if (!string.IsNullOrEmpty(price))
                    label = $"{label}, {price}";

                // Only while a survey/study station is open (alchemy survey table OR the
                // research/study table), tell which items still pay study points the first time
                // they're studied — sighted players read this off the tooltip. It's only useful
                // when you're actually at the table deciding what to study, so we gate it there
                // rather than narrating it over every bag and chest.
                if (GUIAccessibility.IsStudyStationOpen())
                {
                    var study = DescribeStudyReward(cell.item?.definition);
                    if (!string.IsNullOrEmpty(study))
                        label = $"{label}, {study}";
                }

                // Greyed (inactive) cells can't be moved into an offer: on the Buy side the item
                // is tier-locked (vendor won't sell it yet), on the Sell side the vendor won't buy
                // it. The game disables the press, so without this marker the player just hears a
                // misleading "even trade" after pressing. Call it out up front, and explain *why*
                // it's locked (tier, item type, etc.) so the player knows what to do about it.
                if (gui is VendorGUI vguiLock && cell.is_inactive_state)
                {
                    var reason = VendorLockReason(cell.item?.definition, vguiLock, panel);
                    label = string.IsNullOrEmpty(reason)
                        ? $"{label}, not available"
                        : $"{label}, not available, {reason}";
                }

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
            if (gui is ChestGUI chest)
            {
                var chestPanel = cell.GetComponentInParent<InventoryPanelGUI>();
                if (chestPanel == chest.chest_panel) return ("Chest", 0);
                if (chestPanel == chest.player_panel) return ("Inventory", 1);
            }

            // The vendor screen has two panels (stock you can buy, your inventory to sell)
            // plus two offer widgets (the two sides of the deal being assembled). The offer
            // widgets are bare InventoryWidgets with no InventoryPanelGUI parent, so check
            // the cell's owning widget against the vendor's offer widgets first.
            if (gui is VendorGUI vendor)
            {
                var widget = cell.GetComponentInParent<InventoryWidget>();
                if (widget != null && widget == vendor.player_offer_widget) return ("Your offer", 2);
                if (widget != null && widget == vendor.vendor_offer_widget) return ("Vendor offer", 3);

                var vendorPanel = cell.GetComponentInParent<InventoryPanelGUI>();
                if (vendorPanel == vendor.vendor_panel) return ("Buy", 0);
                if (vendorPanel == vendor.player_panel) return ("Sell", 1);
            }

            var panel = cell.GetComponentInParent<InventoryPanelGUI>();
            if (panel == null) return (null, 2);

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
        if (gui is VendorGUI vendor)
        {
            if (panel == vendor.vendor_panel) return "Buy";
            if (panel == vendor.player_panel) return "Sell";
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

            // Body parts in the autopsy grid each carry their own skull score (red = bad, white =
            // good); the on-screen skull pips never voice, so speak the values — "flesh, 2 white".
            var partSkulls = SkullInfo.DescribePart(item);
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
    /// What studying this item at the study table pays out the first time — e.g. "studying gives
    /// 50 blue points, 10 red points". It's a one-time "survey" craft (ItemDefinition.GetSurveyCraft,
    /// id "surv:&lt;item&gt;") whose <c>output</c> holds the tech points (red/green/blue/violet);
    /// once it's in the save's completed_one_time_crafts it pays nothing more, so we return null then.
    ///
    /// IMPORTANT: we always name a tech point by its COLOUR (PointColorName), never by the game's
    /// localized item name. The blue point ("b") is named "Wissenschaft" in the German data, which
    /// collides with the UNRELATED science resource you get from decomposing paper — so calling a
    /// blue-point reward "Wissenschaft" badly confused players. Blue points are just blue points.
    /// Returns null for items with no study reward or already studied.
    /// </summary>
    private static string DescribeStudyReward(ItemDefinition def)
    {
        try
        {
            if (def == null) return null;
            var surveyCraft = def.GetSurveyCraft();
            if (surveyCraft?.output == null) return null;
            if (MainGame.me?.save != null && MainGame.me.save.completed_one_time_crafts.Contains(surveyCraft.id))
                return null;

            var parts = new List<string>();
            foreach (var outp in surveyCraft.output)
            {
                if (outp == null || outp.value <= 0) continue;
                if (!TechDefinition.TECH_POINTS.Contains(outp.id)) continue;

                int value = outp.value;

                // The blue ("b") output always carries a phantom +1 over what the player actually
                // receives in-game (data 51 -> 50 received; data 1 -> 0 received, just a junk filler).
                // Source of the +1 is unclear, but it's consistent, so we strip it: subtract 1 and
                // drop the blue entirely if nothing real is left. Other colours have no such offset.
                if (outp.id == "b")
                {
                    value -= 1;
                    if (value <= 0) continue;
                }

                parts.Add($"{value} {PointColorName(outp.id)}");
            }
            return parts.Count > 0 ? $"studying gives {string.Join(", ", parts)}" : null;
        }
        catch { return null; }
    }

    /// <summary>Fallback spoken name for a tech-point pool when no localized name is available (r/g/b/v colors or gratitude).</summary>
    private static string PointColorName(string id)
    {
        switch (id)
        {
            case "r": return "red points";
            case "g": return "green points";
            case "b": return "blue points";
            case "v": return "violet points";
            case "gratitude_points": return "gratitude points";
            default: return id;
        }
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

            bool buySide = panel == "Buy" || panel == "Vendor offer";
            bool sellSide = panel == "Sell" || panel == "Your offer";
            if (!buySide && !sellSide) return null;

            float per = buySide
                ? vendor.trading.GetSingleItemCostInTraderInventory(cell.item, 0)
                : vendor.trading.GetSingleItemCostInPlayerInventory(cell.item, 0);

            var money = GUIAccessibility.MoneyToSpeech(per);
            if (money == "nothing")
                return sellSide ? "vendor pays nothing" : null;

            var suffix = cell.item.value > 1 ? " each" : "";
            return buySide ? $"costs {money}{suffix}" : $"sells for {money}{suffix}";
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
            bool sellSide = panel == "Sell";              // vendor buying from you
            var mods = sellSide ? trader.definition.not_buying : trader.definition.not_selling;

            if (def.product_types == null || def.product_types.Count == 0)
                return "this item can't be traded";

            if (def.product_tier > trader.cur_tier)
                return $"unlocks when this vendor reaches tier {def.product_tier}, "
                     + $"currently tier {trader.cur_tier}; trade more with them to raise it";

            // CanTradeItemType: none of the item's product types are in the vendor's list.
            bool tradesType = def.product_types.Any(t => trader.definition.GetProductTypes().Contains(t));
            if (!tradesType)
                return sellSide ? "this vendor doesn't buy this kind of item"
                                : "this vendor doesn't sell this kind of item";

            foreach (var m in mods)
            {
                if (m.item_name != def.id) continue;
                if (m.tier < 1)
                    return sellSide ? "this vendor never buys this item"
                                    : "this vendor never sells this item";
                if (m.tier == trader.cur_tier)
                    return "locked at the vendor's current tier, unlocks at the next tier";
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
    private static string QualityTierName(ItemDefinition def)
    {
        if (def == null || def.quality_type != ItemDefinition.QualityType.Stars) return null;

        int stars = Mathf.FloorToInt(def.quality);
        switch (stars)
        {
            case 1: return "bronze quality";
            case 2: return "silver quality";
            case 3: return "gold quality";
            case <= 0: return null;
            default: return $"{stars} stars";
        }
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
            AppendPerk(parts, Mathf.RoundToInt(energy), "energy");
            AppendPerk(parts, Mathf.RoundToInt(hp), "health");
            AppendPerk(parts, Mathf.RoundToInt(sanity), "sanity");

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Add "gives N energy" / "drains N energy" to <paramref name="parts"/> when N != 0.</summary>
    private static void AppendPerk(List<string> parts, int value, string label)
    {
        if (value > 0) parts.Add($"gives {value} {label}");
        else if (value < 0) parts.Add($"drains {-value} {label}");
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
    /// A spoken summary (null if the item had no sensible action) and whether the inventory was
    /// closed as a result (a close-on-use item like the teleport stone hides the inventory, so
    /// the caller must not try to re-discover its now-gone cells).
    /// </returns>
    internal static (string summary, bool closedInventory) ActivateInventoryItem(BaseItemCellGUI cell)
    {
        if (cell == null) return (null, false);
        var item = cell.item;
        if (item == null || item.IsEmpty()) return (null, false);

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
                return ($"Opened {name}", false);
            }

            // Usable items (teleport stone, food, potions): use via the game's own path, mirroring
            // InventoryGUI.UseItem — including close_inv_on_use, which hides the inventory so e.g.
            // the teleport map can open cleanly.
            if (def != null && def.can_be_used)
            {
                if (item.GetGrayedCooldownPercent() > 0)
                    return ($"{name} on cooldown", false);

                if (def.close_inv_on_use)
                {
                    GUIElements.me.game_gui.Hide();
                    MainGame.me.player.UseItemFromInventory(item);
                    return ($"Used {name}", true);
                }

                MainGame.me.player.UseItemFromInventory(item);
                try { panel?.Redraw(); } catch { }
                return ($"Used {name}", false);
            }

            // Weapons / equipment: equip, or unequip if already worn.
            if (def != null && (def.IsWeapon() || def.IsEquipment()))
            {
                if (item.durability_state == Item.DurabilityState.Broken)
                    return ($"{name} is broken", false);

                bool equipped = item.is_equipped ||
                                MainGame.me.player.data.secondary_inventory.Contains(item);
                if (equipped)
                {
                    MainGame.me.player.UnEquipItem(item);
                    try { panel?.Redraw(); } catch { }
                    return ($"Unequipped {name}", false);
                }

                MainGame.me.player.EquipItem(item, -1, null);
                try { panel?.Redraw(); } catch { }
                return ($"Equipped {name}", false);
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
            return $"{name} can't be destroyed";

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