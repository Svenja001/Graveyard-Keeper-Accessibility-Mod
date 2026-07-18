namespace QueueEverything;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("p1xel8ted.gyk.restinpatches")]
public class Plugin : BaseUnityPlugin
{
    private const string AdvancedSection    = "── Advanced ──";
    private const string AutoCraftSection   = "── Auto-Craft ──";
    private const string BalanceSection     = "── Balance ──";
    private const string ConvenienceSection = "── Convenience ──";
    private const string UpdatesSection     = "── Updates ──";

    internal static ConfigEntry<bool> AutoCraft { get; private set; }
    internal static Dictionary<CraftCategory, ConfigEntry<bool>> CategoryToggles { get; } = new();

    internal static ConfigEntry<bool> HalfFireRequirements { get; private set; }
    internal static ConfigEntry<bool> HalfCraftOutputs { get; private set; }

    internal static ConfigEntry<bool> AutoMaxMultiQualCrafts { get; private set; }
    internal static ConfigEntry<bool> AutoMaxNormalCrafts { get; private set; }
    internal static ConfigEntry<bool> AutoSelectHighestQualRecipe { get; private set; }
    internal static ConfigEntry<bool> AutoSelectCraftButtonWithController { get; private set; }
    internal static ConfigEntry<bool> ForceMultiCraft { get; private set; }
    internal static ConfigEntry<bool> AllowMultiQualityMultiCraft { get; private set; }

    internal static ConfigEntry<float> FcTimeAdjustment { get; private set; }
    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static bool DebugEnabled;
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }
    internal static TimestampedLogger Log { get; private set; }

    internal static readonly List<WorldGameObject> CurrentlyCrafting = [];

    // Human workbenches that currently have a zombie worker linked. Keeps those crafts out of
    // auto/force-multi so the zombie-completion path doesn't dump outputs on the ground.
    internal static readonly HashSet<string> ZombieOccupiedBenches = new(StringComparer.Ordinal);

    // Multi-output multi-quality crafts (steel chisels, carved busts) that misreport "don't have
    // items" when the calculator locks the star tier. Leave them un-queueable.
    internal static readonly string[] MultiOutCantQueue =
    [
        "chisel_2_2b", "marble_plate_3"
    ];

    internal static readonly string[] UnSafeCraftDefPartials =
    [
        "0_to_1", "1_to_2", "2_to_3", "3_to_4", "4_to_5", "upgr_to", "_to_lantern_",
        "rem_grave", "soul_workbench_craft", "burgers_place", "beer_barrels_place",
        "remove", "refugee", "upgrade", "fountain", "blockage", "obstacle",
        "builddesk", "fix", "broken", "elevator", "refugee", "Remove",
        "repair_", "place_tent", "find_zombie"
    ];

    internal static readonly string[] UnSafeCraftObjects =
    [
        "mf_crematorium_corp", "garden_builddesk", "tree_garden_builddesk", "mf_crematorium", "grave_ground",
        "tile_church_semicircle_2floors", "mf_grindstone_1", "zombie_garden_desk_1", "zombie_garden_desk_2", "zombie_garden_desk_3",
        "zombie_vineyard_desk_1", "zombie_vineyard_desk_2", "zombie_vineyard_desk_3", "graveyard_builddesk", "blockage_H_low", "blockage_V_low",
        "blockage_H_high", "blockage_V_high", "wood_obstacle_v", "refugee_camp_garden_bed", "refugee_camp_garden_bed_1", "refugee_camp_garden_bed_2",
        "refugee_camp_garden_bed_3", "carrot_box", "elevator_top", "zombie_crafting_table", "mf_balsamation", "mf_balsamation_1", "mf_balsamation_2",
        "mf_balsamation_3", "soul_workbench_craft", "grow_desk_planting", "grow_vineyard_planting",
        "mf_furnace_0", "mf_furnace_1", "mf_distcube_2_clay",
        "lantern_1_clone", "lantern_2_clone",
        "beegarden_table_broken", "swamp_table_constr", "build_vendor_tent_and_stall", "mill_broken_obj",
        "steep_yellow_blockage_R_o", "steep_marble", "steep_marble_2", "steep_stone",
        "garden_of_stones_place", "lantern_place",
        "crafting_skull", "crafting_skull_2",
        "soul_workbench", "soul_container_place_2", "soul_container_2_place_2", "soul_container_3_place_2",
        "test_obj_2"
    ];

    internal static bool AlreadyRun { get; set; }
    internal static bool CcAlreadyRun { get; set; }
    internal static bool CraftsStarted { get; set; }
    internal static bool ExhaustlessEnabled { get; set; }
    internal static bool FasterCraftEnabled { get; set; }
    internal static bool FasterCraftReloaded { get; set; }
    internal static float TimeAdjustment { get; set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        LogHelper.Log = Log;
        Lang.Init(Assembly.GetExecutingAssembly(), Log);
        InitConfiguration();
        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        DebugWarningDialog.Register(MyPluginInfo.PLUGIN_NAME, () => DebugEnabled);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

    private void InitConfiguration()
    {
        const string fcGuid = "p1xel8ted.gyk.fastercraftreloaded";
        if (Harmony.HasAnyPatches(fcGuid))
        {
            var config = new ConfigFile(Path.Combine(Paths.ConfigPath, $"{fcGuid}.cfg"), true);
            var cg = new ConfigDefinition("3. Speed Settings", "Craft Speed Multiplier");
            FcTimeAdjustment = config.Bind(cg, 2f);
            Log.LogInfo("Loading FasterCraft Reloaded Config");
        }

        Debug = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Logging", false, "debug_logging", order: 10);
        DebugEnabled = Debug.Value;
        Debug.SettingChanged += (_, _) => DebugEnabled = Debug.Value;

        AutoCraft = LocalizedConfig.Bind(Config, AutoCraftSection, "Auto-Craft", true, "auto_craft", order: 100);

        BindCategory(CraftCategory.Alchemy,    "Alchemy",    "cat_alchemy",    99);
        BindCategory(CraftCategory.Cooking,    "Cooking",    "cat_cooking",    98);
        BindCategory(CraftCategory.Study,      "Study",      "cat_study",      97);
        BindCategory(CraftCategory.Metalwork,  "Metalwork",  "cat_metalwork",  96);
        BindCategory(CraftCategory.Morgue,     "Morgue",     "cat_morgue",     95);
        BindCategory(CraftCategory.Carpentry,  "Carpentry",  "cat_carpentry",  94);
        BindCategory(CraftCategory.Sermons,    "Sermons",    "cat_sermons",    93);
        BindCategory(CraftCategory.Printing,   "Printing",   "cat_printing",   92);
        BindCategory(CraftCategory.Winemaking, "Winemaking", "cat_winemaking", 91);
        BindCategory(CraftCategory.Pottery,    "Pottery",    "cat_pottery",    90);
        BindCategory(CraftCategory.Misc,       "Misc",       "cat_misc",       89);

        HalfFireRequirements = LocalizedConfig.Bind(Config, BalanceSection, "Half Fire Requirements", true, "half_fire_requirements", order: 100);

        HalfCraftOutputs = LocalizedConfig.Bind(Config, BalanceSection, "Half Research Point Outputs", true, "half_research_point_outputs", order: 99);

        AutoMaxMultiQualCrafts = LocalizedConfig.Bind(Config, ConvenienceSection, "Auto Max Multi-Quality Crafts", true, "auto_max_multi_quality_crafts", order: 100);

        AutoMaxNormalCrafts = LocalizedConfig.Bind(Config, ConvenienceSection, "Auto Max Normal Crafts", false, "auto_max_normal_crafts", order: 99);

        AutoSelectHighestQualRecipe = LocalizedConfig.Bind(Config, ConvenienceSection, "Auto Select Highest Quality Recipe", true, "auto_select_highest_quality_recipe", order: 98);

        AutoSelectCraftButtonWithController = LocalizedConfig.Bind(Config, ConvenienceSection, "Auto Select Craft Button With Controller", true, "auto_select_craft_button_with_controller", order: 97);

        ForceMultiCraft = LocalizedConfig.Bind(Config, ConvenienceSection, "Force Multi Craft", true, "force_multi_craft", order: 96);

        AllowMultiQualityMultiCraft = LocalizedConfig.Bind(Config, ConvenienceSection, "Allow Multi-Quality Multicraft", false, "allow_multiquality_multicraft", order: 95);

        AutoCraft.SettingChanged      += OnCraftSettingChanged;
        HalfFireRequirements.SettingChanged += OnCraftSettingChanged;
        HalfCraftOutputs.SettingChanged     += OnCraftSettingChanged;
        ForceMultiCraft.SettingChanged      += OnCraftSettingChanged;
        AllowMultiQualityMultiCraft.SettingChanged += OnCraftSettingChanged;
        AllowMultiQualityMultiCraft.SettingChanged += (_, _) =>
        {
            if (AllowMultiQualityMultiCraft.Value) ShowMultiQualityMulticraftWarning();
        };
        foreach (var entry in CategoryToggles.Values)
        {
            entry.SettingChanged += OnCraftSettingChanged;
        }

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates", order: 0);
        return;

        void BindCategory(CraftCategory category, string label, string langKey, int order)
        {
            var entry = LocalizedConfig.Bind(Config, AutoCraftSection, label, true, langKey, order: order, dispNamePrefix: "    └ ");
            CategoryToggles[category] = entry;
        }
    }

    internal static bool IsCategoryEnabled(CraftCategory category)
    {
        if (!AutoCraft.Value)
        {
            return false;
        }

        return CategoryToggles.TryGetValue(category, out var entry) && entry.Value;
    }

    internal static bool AnyAutoCraftCategoryEnabled()
    {
        if (!AutoCraft.Value)
        {
            return false;
        }

        foreach (var entry in CategoryToggles.Values)
        {
            if (entry.Value)
            {
                return true;
            }
        }

        return false;
    }

    private static void OnCraftSettingChanged(object sender, EventArgs e)
    {
        var changedKey = (sender as ConfigEntryBase)?.Definition.Key ?? "unknown";
        if (!CcAlreadyRun || !MainGame.game_started || GameBalance.me == null)
        {
            if (DebugEnabled)
            {
                WriteLog($"[OnCraftSettingChanged] '{changedKey}' changed but mutations not yet applied; deferring until game starts.");
            }
            return;
        }

        if (DebugEnabled)
        {
            WriteLog($"[OnCraftSettingChanged] '{changedKey}' changed - queued for next-frame re-apply.");
        }

        CraftComponentPatches.PendingFullReapply = true;
    }

    internal static bool IsUnsafeDefinition(CraftDefinition _craftDefinition)
    {
        var zombieCraft = _craftDefinition.craft_in.Any(craftIn => craftIn.Contains("zombie"));
        var refugeeCraft = _craftDefinition.craft_in.Any(craftIn => craftIn.Contains("refugee"));
        var unsafeOne = UnSafeCraftDefPartials.Any(_craftDefinition.id.Contains);
        var unsafeTwo = !_craftDefinition.icon.Contains("fire") && _craftDefinition.craft_in.Any(craftIn => UnSafeCraftObjects.Contains(craftIn));
        var unsafeThree = MultiOutCantQueue.Any(_craftDefinition.id.Contains);
        var zombieOccupied = ZombieOccupiedBenches.Count > 0 &&
                             _craftDefinition.craft_in.Any(craftIn => ZombieOccupiedBenches.Contains(craftIn));

        if (zombieCraft || refugeeCraft || unsafeOne || unsafeTwo || unsafeThree || zombieOccupied)
        {
            return true;
        }

        return false;
    }

    private static void ShowMultiQualityMulticraftWarning()
    {
        if (GUIElements.me?.dialog == null) return;
        GUIElements.me.dialog.OpenOK(Lang.Get("mqmc.warning.title"), null, Lang.Get("mqmc.warning.body"), true, string.Empty);
    }

    internal static void WriteLog(string message, bool error = false)
    {
        if (error)
        {
            LogHelper.Error(message);
        }
        else
        {
            LogHelper.Info(message);
        }
    }

}
