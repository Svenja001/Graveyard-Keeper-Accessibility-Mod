namespace TheSeedEqualizer;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    // Section names sort alphabetically in CM. Bind order picks the first one as the default expanded view.
    private const string AdvancedSection       = "── Advanced ──";
    private const string PlayerGardensSection  = "── Player Gardens ──";
    private const string ZombieGardensSection  = "── Zombie Gardens ──";
    private const string RefugeeGardensSection = "── Refugee Gardens ──";
    private const string WasteSection          = "── Waste ──";
    private const string AllGardensSection     = "── All Gardens ──";
    private const string TrackingSection       = "── Tracking ──";
    private const string UpdatesSection        = "── Updates ──";

    internal static TimestampedLogger Log { get; private set; }

    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static bool DebugEnabled;

    internal static ConfigEntry<bool> ModifyPlayerGardens { get; private set; }
    internal static ConfigEntry<bool> ModifyZombieGardens { get; private set; }
    internal static ConfigEntry<bool> ModifyZombieVineyards { get; private set; }
    internal static ConfigEntry<bool> ModifyRefugeeGardens { get; private set; }
    internal static ConfigEntry<bool> AddWasteToZombieGardens { get; private set; }
    internal static ConfigEntry<bool> AddWasteToZombieVineyards { get; private set; }
    internal static ConfigEntry<bool> BreakEvenPlayerGardens { get; private set; }
    internal static ConfigEntry<bool> BreakEvenZombieGardens { get; private set; }
    internal static ConfigEntry<bool> BreakEvenZombieVineyards { get; private set; }
    internal static ConfigEntry<bool> BreakEvenRefugeeGardens { get; private set; }
    internal static ConfigEntry<bool> BoostPotentialSeedOutput { get; private set; }
    internal static ConfigEntry<bool> BoostGrowSpeedWhenRaining { get; private set; }
    internal static ConfigEntry<bool> TrackPlantCycles { get; private set; }
    internal static ConfigEntry<bool> DebugTracking { get; private set; }
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
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);

        ModifyPlayerGardens.SettingChanged       += OnSeedSettingChanged;
        ModifyZombieGardens.SettingChanged       += OnSeedSettingChanged;
        ModifyZombieVineyards.SettingChanged     += OnSeedSettingChanged;
        ModifyRefugeeGardens.SettingChanged      += OnSeedSettingChanged;
        AddWasteToZombieGardens.SettingChanged   += OnSeedSettingChanged;
        AddWasteToZombieVineyards.SettingChanged += OnSeedSettingChanged;
        BreakEvenPlayerGardens.SettingChanged    += OnSeedSettingChanged;
        BreakEvenZombieGardens.SettingChanged    += OnSeedSettingChanged;
        BreakEvenZombieVineyards.SettingChanged  += OnSeedSettingChanged;
        BreakEvenRefugeeGardens.SettingChanged   += OnSeedSettingChanged;
        BoostPotentialSeedOutput.SettingChanged  += OnSeedSettingChanged;
    }

    private static void OnSeedSettingChanged(object sender, EventArgs e) => Helpers.Reconcile();

    private void InitConfiguration()
    {
        Debug = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Logging", false, "debug_logging", order: 100);
        DebugEnabled = Debug.Value;
        Debug.SettingChanged += (_, _) => DebugEnabled = Debug.Value;

        ModifyPlayerGardens = LocalizedConfig.Bind(Config, PlayerGardensSection, "Modify Player Gardens", false, "modify_player_gardens", order: 100);
        BreakEvenPlayerGardens = LocalizedConfig.Bind(Config, PlayerGardensSection, "Player Gardens Break Even", false, "break_even_player_gardens", order: 99);

        ModifyZombieGardens = LocalizedConfig.Bind(Config, ZombieGardensSection, "Modify Zombie Gardens", true, "modify_zombie_gardens", order: 100);
        BreakEvenZombieGardens = LocalizedConfig.Bind(Config, ZombieGardensSection, "Zombie Gardens Break Even", false, "break_even_zombie_gardens", order: 99);
        ModifyZombieVineyards = LocalizedConfig.Bind(Config, ZombieGardensSection, "Modify Zombie Vineyards", true, "modify_zombie_vineyards", order: 98);
        BreakEvenZombieVineyards = LocalizedConfig.Bind(Config, ZombieGardensSection, "Zombie Vineyards Break Even", false, "break_even_zombie_vineyards", order: 97);

        ModifyRefugeeGardens = LocalizedConfig.Bind(Config, RefugeeGardensSection, "Modify Refugee Gardens", true, "modify_refugee_gardens", order: 100);
        BreakEvenRefugeeGardens = LocalizedConfig.Bind(Config, RefugeeGardensSection, "Refugee Gardens Break Even", false, "break_even_refugee_gardens", order: 99);

        AddWasteToZombieGardens = LocalizedConfig.Bind(Config, WasteSection, "Add Waste To Zombie Gardens", true, "add_waste_to_zombie_gardens", order: 100);
        AddWasteToZombieVineyards = LocalizedConfig.Bind(Config, WasteSection, "Add Waste To Zombie Vineyards", true, "add_waste_to_zombie_vineyards", order: 99);

        BoostPotentialSeedOutput = LocalizedConfig.Bind(Config, AllGardensSection, "Boost Potential Seed Output", true, "boost_potential_seed_output", order: 100);
        BoostGrowSpeedWhenRaining = LocalizedConfig.Bind(Config, AllGardensSection, "Boost Grow Speed When Raining", true, "boost_grow_speed_when_raining", order: 99);

        TrackPlantCycles = LocalizedConfig.Bind(Config, TrackingSection, "Track Plant Cycles", true, "track_plant_cycles", order: 100);
        DebugTracking = LocalizedConfig.Bind(Config, TrackingSection, "Verbose Tracking Logs", false, "verbose_tracking_logs", order: 99);

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates", order: 100);
    }

}
