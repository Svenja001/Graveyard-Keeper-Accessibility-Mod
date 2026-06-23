namespace WheresMaStorage;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    // Section names sort alphabetically in CM's display.
    private const string AdvancedSection = "── Advanced ──";
    private const string InventorySection = "── Inventory ──";
    private const string ItemStackingSection = "── Item Stacking ──";
    private const string GameplaySection = "── Gameplay ──";
    private const string UISection = "── UI ──";
    private const string UpdatesSection = "── Updates ──";

    internal static TimestampedLogger Log { get; private set; }
    internal static bool DebugEnabled;
    internal static ConfigEntry<bool> ModifyInventorySize { get; private set; }
    internal static ConfigEntry<bool> EnableGraveItemStacking { get; private set; }
    internal static ConfigEntry<bool> EnablePenPaperInkStacking { get; private set; }
    internal static ConfigEntry<bool> EnableChiselStacking { get; private set; }
    internal static ConfigEntry<bool> EnableToolStacking { get; private set; }
    internal static ConfigEntry<bool> EnablePrayerStacking { get; private set; }
    internal static ConfigEntry<bool> EnableWeaponStacking { get; private set; }
    internal static ConfigEntry<bool> EnableEquipmentStacking { get; private set; }
    internal static ConfigEntry<bool> AllowHandToolDestroy { get; private set; }
    internal static ConfigEntry<bool> ModifyStackSize { get; private set; }
    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static ConfigEntry<KeyboardShortcut> DebugTagScanKeybind { get; private set; }
    internal static ConfigEntry<float> DebugTagScanRadius { get; private set; }
    internal static ConfigEntry<bool> SharedInventory { get; private set; }
    internal static ConfigEntry<bool> SortByDistanceFromCrafter { get; private set; }
    internal static ConfigEntry<bool> ExcludeWellsFromSharedInventory { get; private set; }
    internal static ConfigEntry<bool> ExcludeZombieMillFromSharedInventory { get; private set; }
    internal static ConfigEntry<bool> ExcludeQuarryFromSharedInventory { get; private set; }
    internal static ConfigEntry<bool> DontShowEmptyRowsInInventory { get; private set; }
    internal static ConfigEntry<bool> ShowUsedSpaceInTitles { get; private set; }
    internal static ConfigEntry<bool> DisableInventoryDimming { get; private set; }
    internal static ConfigEntry<bool> ShowWorldZoneInTitles { get; private set; }
    internal static ConfigEntry<bool> HideInvalidSelections { get; private set; }
    internal static ConfigEntry<bool> RemoveGapsBetweenSections { get; private set; }
    internal static ConfigEntry<bool> RemoveGapsBetweenSectionsVendor { get; private set; }
    internal static ConfigEntry<bool> ShowOnlyPersonalInventory { get; private set; }
    internal static ConfigEntry<int> AdditionalPlayerInventorySpace { get; private set; }
    internal static ConfigEntry<int> AdditionalContainerInventorySpace { get; private set; }
    internal static ConfigEntry<int> StackSizeForStackables { get; private set; }
    internal static ConfigEntry<bool> HideStockpileWidgets { get; private set; }
    internal static ConfigEntry<bool> HideTavernWidgets { get; private set; }
    internal static ConfigEntry<bool> HideSoulWidgets { get; private set; }
    internal static ConfigEntry<bool> HideWarehouseShopWidgets { get; private set; }
    internal static ConfigEntry<bool> HideBagWidgets { get; private set; }
    internal static ConfigEntry<bool> CollectDropsOnGameLoad { get; private set; }
    internal static ConfigEntry<DropHandlingMode> DropHandlingOnGameLoad { get; private set; }
    internal static ConfigEntry<int> NearHouseDumpZoneRadius { get; private set; }
    internal static ConfigEntry<float> PlayerLootMagnetRange { get; private set; }
    public static ConfigEntry<bool> AllowZombiesAccessToSharedInventory { get; set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }


    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        LogHelper.Log = Log;
        Lang.Init(Assembly.GetExecutingAssembly(), Log);
        InitConfiguration();
        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        DebugWarningDialog.Register(MyPluginInfo.PLUGIN_NAME, () => DebugEnabled);
        ConflictWarningRegistry.Register(MyPluginInfo.PLUGIN_NAME, () => new[]
        {
            new ConflictEntry(
                theirGuid: "com.oyasumi.infinitestack",
                theirName: "Oyasumi Infinite Stack",
                feature: Lang.Get("Conflict.Oyasumi.Feature"),
                severity: ConflictSeverity.Race,
                note: Lang.Get("Conflict.Oyasumi.Note")),
            new ConflictEntry(
                theirGuid: "MoreInventorySlots",
                theirName: "More Inventory Slots",
                feature: Lang.Get("Conflict.MoreInventorySlots.Feature"),
                severity: ConflictSeverity.Race,
                note: Lang.Get("Conflict.MoreInventorySlots.Note")),
        });
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

    private void InitConfiguration()
    {
        Debug = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Logging", false, "debug_logging", order: 90);
        DebugEnabled = Debug.Value;
        Debug.SettingChanged += (_, _) => DebugEnabled = Debug.Value;

        DebugTagScanKeybind = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Tag Scan Keybind", new KeyboardShortcut(KeyCode.F7), "debug_tag_scan_keybind", order: 80, dispNamePrefix: "    └ ");

        DebugTagScanRadius = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Tag Scan Radius", 10f, "debug_tag_scan_radius", new AcceptableValueRange<float>(1f, 100f), order: 79, dispNamePrefix: "    └ ");

        SharedInventory = LocalizedConfig.Bind(Config, InventorySection, "Shared Inventory", true, "shared_inventory", order: 100);

        AllowZombiesAccessToSharedInventory = LocalizedConfig.Bind(Config, InventorySection, "Allow Zombies Access To Shared Inventory", true, "allow_zombies_access", order: 99, dispNamePrefix: "    └ ");
        AllowZombiesAccessToSharedInventory.SettingChanged += (_, _) => Fields.InventoriesLoaded = false;

        ExcludeWellsFromSharedInventory = LocalizedConfig.Bind(Config, InventorySection, "Exclude Wells From Shared Inventory", true, "exclude_wells", order: 98, dispNamePrefix: "    └ ");
        ExcludeWellsFromSharedInventory.SettingChanged += (_, _) => Fields.InventoriesLoaded = false;

        ExcludeZombieMillFromSharedInventory = LocalizedConfig.Bind(Config, InventorySection, "Exclude Zombie Mill From Shared Inventory", true, "exclude_zombie_mill", order: 97, dispNamePrefix: "    └ ");
        ExcludeZombieMillFromSharedInventory.SettingChanged += (_, _) => Fields.InventoriesLoaded = false;

        ExcludeQuarryFromSharedInventory = LocalizedConfig.Bind(Config, InventorySection, "Exclude Quarry From Shared Inventory", true, "exclude_quarry", order: 96, dispNamePrefix: "    └ ");
        ExcludeQuarryFromSharedInventory.SettingChanged += (_, _) => Fields.InventoriesLoaded = false;

        SortByDistanceFromCrafter = LocalizedConfig.Bind(Config, InventorySection, "Sort By Distance From Crafter", true, "sort_by_distance_from_crafter", order: 95, dispNamePrefix: "    └ ");

        ModifyInventorySize = LocalizedConfig.Bind(Config, InventorySection, "Modify Inventory Size", true, "modify_inventory_size", order: 90);
        ModifyInventorySize.SettingChanged += (_, _) =>
        {
            Fields.InventorySizesDirty = true;
            Fields.InventoriesLoaded = false;
        };

        AdditionalPlayerInventorySpace = LocalizedConfig.Bind(Config, InventorySection, "Additional Player Inventory Space", 20, "additional_player_inventory_space", new AcceptableValueRange<int>(0, 500), order: 89, dispNamePrefix: "    └ ");
        AdditionalPlayerInventorySpace.SettingChanged += (_, _) =>
        {
            Fields.InventorySizesDirty = true;
            Fields.InventoriesLoaded = false;
        };

        AdditionalContainerInventorySpace = LocalizedConfig.Bind(Config, InventorySection, "Additional Container Inventory Space", 20, "additional_container_inventory_space", new AcceptableValueRange<int>(0, 500), order: 88, dispNamePrefix: "    └ ");
        AdditionalContainerInventorySpace.SettingChanged += (_, _) =>
        {
            Fields.InventorySizesDirty = true;
            Fields.InventoriesLoaded = false;
        };

        ShowOnlyPersonalInventory = LocalizedConfig.Bind(Config, InventorySection, "Show Only Personal Inventory", true, "show_only_personal_inventory", order: 80);
        DontShowEmptyRowsInInventory = LocalizedConfig.Bind(Config, InventorySection, "Dont Show Empty Rows In Inventory", true, "dont_show_empty_rows_in_inventory", order: 79);
        ShowUsedSpaceInTitles = LocalizedConfig.Bind(Config, InventorySection, "Show Used Space In Titles", true, "show_used_space_in_titles", order: 78);
        DisableInventoryDimming = LocalizedConfig.Bind(Config, InventorySection, "Inventory Dimming", true, "inventory_dimming", order: 77);
        ShowWorldZoneInTitles = LocalizedConfig.Bind(Config, InventorySection, "Show World Zone In Titles", true, "show_world_zone_in_titles", order: 76);
        HideInvalidSelections = LocalizedConfig.Bind(Config, InventorySection, "Hide Invalid Selections", true, "hide_invalid_selections", order: 75);
        RemoveGapsBetweenSections = LocalizedConfig.Bind(Config, InventorySection, "Remove Gaps Between Sections", true, "remove_gaps_between_sections", order: 74);
        RemoveGapsBetweenSectionsVendor = LocalizedConfig.Bind(Config, InventorySection, "Remove Gaps Between Sections Vendor", true, "remove_gaps_between_sections_vendor", order: 73);

        ModifyStackSize = LocalizedConfig.Bind(Config, ItemStackingSection, "Modify Stack Size", true, "modify_stack_size", order: 100);
        ModifyStackSize.SettingChanged += (_, _) => Fields.StackSizesDirty = true;

        StackSizeForStackables = LocalizedConfig.Bind(Config, ItemStackingSection, "Stack Size For Stackables", 999, "stack_size_for_stackables", new AcceptableValueRange<int>(1, 999), order: 99, dispNamePrefix: "    └ ");
        StackSizeForStackables.SettingChanged += (_, _) => Fields.StackSizesDirty = true;

        EnableGraveItemStacking = LocalizedConfig.Bind(Config, ItemStackingSection, "Grave Item Stacking", false, "grave_item_stacking", order: 98, dispNamePrefix: "    └ ");
        EnableGraveItemStacking.SettingChanged += (_, _) => Fields.StackSizesDirty = true;

        EnablePenPaperInkStacking = LocalizedConfig.Bind(Config, ItemStackingSection, "Pen Paper Ink Stacking", false, "pen_paper_ink_stacking", order: 97, dispNamePrefix: "    └ ");
        EnablePenPaperInkStacking.SettingChanged += (_, _) => Fields.StackSizesDirty = true;

        EnableChiselStacking = LocalizedConfig.Bind(Config, ItemStackingSection, "Chisel Stacking", false, "chisel_stacking", order: 96, dispNamePrefix: "    └ ");
        EnableChiselStacking.SettingChanged += (_, _) => Fields.StackSizesDirty = true;

        EnableToolStacking = LocalizedConfig.Bind(Config, ItemStackingSection, "Tool Stacking", true, "tool_stacking", order: 95, dispNamePrefix: "    └ ");
        EnableToolStacking.SettingChanged += (_, _) => Fields.StackSizesDirty = true;

        EnablePrayerStacking = LocalizedConfig.Bind(Config, ItemStackingSection, "Prayer Stacking", true, "prayer_stacking", order: 94, dispNamePrefix: "    └ ");
        EnablePrayerStacking.SettingChanged += (_, _) => Fields.StackSizesDirty = true;

        EnableWeaponStacking = LocalizedConfig.Bind(Config, ItemStackingSection, "Weapon Stacking", true, "weapon_stacking", order: 93, dispNamePrefix: "    └ ");
        EnableWeaponStacking.SettingChanged += (_, _) => Fields.StackSizesDirty = true;

        EnableEquipmentStacking = LocalizedConfig.Bind(Config, ItemStackingSection, "Equipment Stacking", true, "equipment_stacking", order: 92, dispNamePrefix: "    └ ");
        EnableEquipmentStacking.SettingChanged += (_, _) => Fields.StackSizesDirty = true;

        AllowHandToolDestroy = LocalizedConfig.Bind(Config, GameplaySection, "Allow Hand Tool Destroy", true, "allow_hand_tool_destroy", order: 100);
        AllowHandToolDestroy.SettingChanged += (_, _) => Fields.ToolDestroyDirty = true;

        CollectDropsOnGameLoad = LocalizedConfig.Bind(Config, GameplaySection, "Collect Drops On Game Load", true, "collect_drops_on_game_load", order: 99);

        DropHandlingOnGameLoad = LocalizedConfig.Bind(Config, GameplaySection, "Drop Handling On Game Load", DropHandlingMode.CollectToInventory, "drop_handling_on_game_load", order: 98, dispNamePrefix: "    └ ");

        NearHouseDumpZoneRadius = LocalizedConfig.Bind(Config, GameplaySection, "Near-House Dump Zone Radius", 8, "near_house_dump_zone_radius", new AcceptableValueRange<int>(1, 30), order: 97, dispNamePrefix: "    └ ");

        PlayerLootMagnetRange = LocalizedConfig.Bind(Config, GameplaySection, "Player Loot Magnet Range", 2.0f, "player_loot_magnet_range", new AcceptableValueRange<float>(2.0f, 20f), order: 95);
        // Snap the slider to a 0.25-tile grid. The guard prevents the assignment
        // from re-firing SettingChanged into infinite recursion. Also called once
        // immediately to migrate any pre-existing off-grid persisted value.
        void SnapMagnetRange()
        {
            var v = PlayerLootMagnetRange.Value;
            var stepped = Mathf.Round(v / 0.25f) * 0.25f;
            if (Mathf.Abs(v - stepped) > 1e-4f) PlayerLootMagnetRange.Value = stepped;
        }
        PlayerLootMagnetRange.SettingChanged += (_, _) => SnapMagnetRange();
        SnapMagnetRange();

        HideStockpileWidgets = LocalizedConfig.Bind(Config, UISection, "Hide Stockpile Widgets", true, "hide_stockpile_widgets", order: 100);
        HideTavernWidgets = LocalizedConfig.Bind(Config, UISection, "Hide Tavern Widgets", true, "hide_tavern_widgets", order: 99);
        HideSoulWidgets = LocalizedConfig.Bind(Config, UISection, "Hide Soul Widgets", true, "hide_soul_widgets", order: 98);
        HideWarehouseShopWidgets = LocalizedConfig.Bind(Config, UISection, "Hide Warehouse Shop Widgets", true, "hide_warehouse_shop_widgets", order: 97);
        HideBagWidgets = LocalizedConfig.Bind(Config, UISection, "Hide Bag Widgets", true, "hide_bag_widgets", order: 96);

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates", order: 100);

        Fields.GameBalanceAlreadyRun = false;
    }


    public void Update()
    {
        if (!MainGame.game_started) return;

        if (DebugTagScanKeybind.Value.IsDown())
        {
            Helpers.LogNearbyTags(DebugTagScanRadius.Value);
        }

        if (Fields.InventorySizesDirty && !Fields.ShrinkDialogOpen)
        {
            // Drain only when no shrink dialog is on screen - otherwise we'd queue up another
            // plan against the user's pending answer. The dialog's callbacks clear ShrinkDialogOpen.
            Fields.InventorySizesDirty = false;
            Helpers.UpdateInventorySizes();
            Helpers.HandleInventorySizesDirty();
        }

        if (Fields.StackSizesDirty)
        {
            Fields.StackSizesDirty = false;
            Helpers.UpdateStackSizes();
        }

        if (Fields.ToolDestroyDirty)
        {
            Fields.ToolDestroyDirty = false;
            Helpers.UpdateToolDestroy();
        }

        // Per-frame natural-shrink + legacy-recovery safety net for the player.
        // Cheap (one int compare and a branch) when nothing's changed.
        Helpers.ApplyPlayerInventorySize();

        if (Fields.InventoriesLoaded) return;
        if (Fields.LoadInventoriesCoroutine != null) return;
        Helpers.RunWmsTasks();
    }
}

public enum DropHandlingMode
{
    CollectToInventory,
    MoveNearKeepersHouse,
}
