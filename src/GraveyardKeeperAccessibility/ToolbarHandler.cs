namespace GraveyardKeeperAccessibility;

/// <summary>
/// Makes the 4-slot quick-use hotbar (number keys 1-4) accessible. Sighted players drag a
/// usable item (food, potions, teleport stone…) onto one of four bottom-bar slots and then
/// tap 1-4 to use it without opening the inventory. None of that was reachable blind.
///
/// This handler adds three things:
///  • <b>Use feedback</b> — when a slot is triggered in the world we speak the result
///    ("Used bread, 2 left" / "Slot 2 empty" / "No bread left"). The actual use is the game's
///    own <see cref="BaseCharacterComponent"/>.ProcessToolbar → UseItemFromToolbar; we only patch
///    it to announce.
///  • <b>Assignment</b> — while the inventory is open and an item cell is focused, pressing 1-4
///    puts that item in the matching slot (or clears it if it was already there). Driven straight
///    into the save's toolbar the same way EquipToToolbarGUI does (SetToolbarEquipped).
///  • <b>Read-out</b> — a world key reads all four slots at once.
///
/// The hotbar is stored as four item ids in <c>MainGame.me.save.equipped_items</c>; see
/// ToolbarSetGUI / EquipToToolbarGUI / GameSave in the decompiled source.
/// </summary>
internal static class ToolbarHandler
{
    private const int SlotCount = 4;
    private static ManualLogSource _log;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _log?.LogInfo("[TOOLBAR] ToolbarHandler initialized");
    }

    // ---- Use feedback (patched onto UseItemFromToolbar) -------------------------------

    /// <summary>Before-use snapshot passed from the prefix to the postfix via Harmony __state.</summary>
    internal struct UseState
    {
        internal bool Player;
        internal string Id;
        internal int Before;
    }

    /// <summary>
    /// Snapshot the equipped id and how many the player holds, before the game uses it. Gated to
    /// the player's own character component (NPCs share the type but never fire toolbar keys).
    /// </summary>
    internal static void UseItemFromToolbar_Prefix(BaseCharacterComponent __instance, int index, out UseState __state)
    {
        __state = default;
        try
        {
            if (__instance?.wgo == null || !__instance.wgo.is_player) return;
            if (index < 0 || index >= SlotCount) return;

            __state.Player = true;
            __state.Id = MainGame.me.save.GetEquippedItem(index);
            __state.Before = string.IsNullOrEmpty(__state.Id)
                ? 0
                : MainGame.me.player.data.GetTotalCount(__state.Id);
        }
        catch { __state = default; }
    }

    /// <summary>
    /// Announce the outcome: empty slot, out of stock, a successful use with the remaining count,
    /// or (when nothing was consumed) that it couldn't be used right now. The health/energy gain
    /// itself is spoken separately by <see cref="HealthEnergyAnnouncer"/>.
    /// </summary>
    internal static void UseItemFromToolbar_Postfix(BaseCharacterComponent __instance, int index, UseState __state)
    {
        try
        {
            if (!__state.Player) return;

            if (string.IsNullOrEmpty(__state.Id))
            {
                ScreenReader.Say($"Slot {index + 1} empty");
                return;
            }

            var name = ItemName(__state.Id);
            if (__state.Before <= 0)
            {
                ScreenReader.Say($"No {name} left");
                return;
            }

            int after = MainGame.me.player.data.GetTotalCount(__state.Id);
            if (after < __state.Before)
                ScreenReader.Say(after > 0 ? $"Used {name}, {after} left" : $"Used {name}, none left");
            else
                // Count unchanged means the use had no effect (e.g. a bar that's already full). The
                // game itself refuses to put the reusable teleport stone on the hotbar, so a
                // "does something without being consumed" item never lands here.
                ScreenReader.Say($"Can't use {name} now");
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[TOOLBAR] use announce threw: {ex.Message}");
        }
    }

    // ---- Assignment from the inventory ------------------------------------------------

    /// <summary>
    /// Assign <paramref name="item"/> to hotbar slot <paramref name="slot"/> (0-based). If the item
    /// is already in that slot it's cleared instead (a toggle). Returns a spoken confirmation, or an
    /// explanation when the item isn't hotbar-eligible. Mirrors the game's own eligibility test in
    /// InventoryGUI.OnItemEquip: only usable items without a cooldown-expression can go on the
    /// number bar (weapons/armour use the separate tool belt).
    /// </summary>
    internal static string AssignItemToSlot(Item item, int slot)
    {
        try
        {
            if (item == null || item.IsEmpty()) return null;
            if (slot < 0 || slot >= SlotCount) return null;

            var save = MainGame.me?.save;
            if (save == null) return null;

            var def = item.definition;
            var name = ScreenReader.StripNguiCodes(def?.GetItemName() ?? item.id)?.Trim();
            if (string.IsNullOrEmpty(name)) name = item.id;

            bool eligible = def != null && def.can_be_used
                            && (def.cooldown == null || !def.cooldown.has_expression);
            if (!eligible)
                return $"{name} can't go in the hotbar";

            // Already in this slot -> clear it (toggle off).
            if (save.GetEquippedItem(slot) == item.id)
            {
                save.UnEquip(slot);
                RedrawToolbars();
                return $"Removed {name} from slot {slot + 1}";
            }

            // SetToolbarEquipped clears any other slot holding this id first, so an item only ever
            // occupies one slot.
            save.SetToolbarEquipped(item.id, slot);
            RedrawToolbars();
            return $"Assigned {name} to slot {slot + 1}";
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[TOOLBAR] assign threw: {ex.Message}");
            return null;
        }
    }

    // ---- Read-out ---------------------------------------------------------------------

    /// <summary>Speak all four slots: "Hotbar: slot 1 bread, 3. Slot 2 empty. …".</summary>
    internal static void ReadHotbar()
    {
        try
        {
            var save = MainGame.me?.save;
            if (save == null) return;

            var parts = new List<string>(SlotCount);
            for (int i = 0; i < SlotCount; i++)
            {
                var id = save.GetEquippedItem(i);
                if (string.IsNullOrEmpty(id))
                {
                    parts.Add($"Slot {i + 1} empty");
                    continue;
                }

                var name = ItemName(id);
                int count = 0;
                try { count = MainGame.me.player.data.GetTotalCount(id); } catch { }
                parts.Add(count > 0 ? $"Slot {i + 1} {name}, {count}" : $"Slot {i + 1} {name}, none left");
            }

            ScreenReader.Say("Hotbar: " + string.Join(". ", parts));
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[TOOLBAR] read threw: {ex.Message}");
        }
    }

    // ---- Helpers ----------------------------------------------------------------------

    /// <summary>Localized display name for a bare item id, falling back to the id itself.</summary>
    private static string ItemName(string id)
    {
        try
        {
            var def = GameBalance.me?.GetData<ItemDefinition>(id);
            var name = ScreenReader.StripNguiCodes(def?.GetItemName() ?? id)?.Trim();
            return string.IsNullOrEmpty(name) ? id : name;
        }
        catch { return id; }
    }

    /// <summary>
    /// Refresh any on-screen toolbar widgets after we change the equipped items, so the visual
    /// (which sighted onlookers still see) matches the save. Purely cosmetic for a blind player,
    /// and best-effort — a throw here must never block the spoken confirmation.
    /// </summary>
    private static void RedrawToolbars()
    {
        try
        {
            var hud = GUIElements.me?.hud;
            if (hud?.toolbar != null && hud.toolbar.gameObject.activeInHierarchy)
                hud.toolbar.Redraw();
        }
        catch { }
    }
}
