namespace DecompDelight;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string AdvancedSection = "── Advanced ──";
    private const string ColorsSection   = "── Colors ──";
    private const string UpdatesSection  = "── Updates ──";

    internal static TimestampedLogger Log { get; private set; }
    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static bool DebugEnabled;
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    private static ConfigEntry<Color> SlowingColor { get; set; }
    private static ConfigEntry<Color> AccelerationColor { get; set; }
    private static ConfigEntry<Color> HealthColor { get; set; }
    private static ConfigEntry<Color> DeathColor { get; set; }
    private static ConfigEntry<Color> OrderColor { get; set; }
    private static ConfigEntry<Color> ToxicColor { get; set; }
    private static ConfigEntry<Color> ChaosColor { get; set; }
    private static ConfigEntry<Color> LifeColor { get; set; }
    private static ConfigEntry<Color> ElectricColor { get; set; }
    private static ConfigEntry<Color> SilverColor { get; set; }
    private static ConfigEntry<Color> WhiteColor { get; set; }
    private static ConfigEntry<Color> WaterColor { get; set; }
    private static ConfigEntry<Color> OilColor { get; set; }
    private static ConfigEntry<Color> BloodColor { get; set; }
    private static ConfigEntry<Color> SaltColor { get; set; }
    private static ConfigEntry<Color> AshColor { get; set; }
    private static ConfigEntry<Color> AlcoholColor { get; set; }
    private static ConfigEntry<Color> GoldColor { get; set; }
    private static ConfigEntry<Color> GraphiteColor { get; set; }

    internal static string SlowingColorHex => Utils.ColorToHex(SlowingColor.Value);
    internal static string AccelerationColorHex => Utils.ColorToHex(AccelerationColor.Value);
    internal static string HealthColorHex => Utils.ColorToHex(HealthColor.Value);
    internal static string DeathColorHex => Utils.ColorToHex(DeathColor.Value);
    internal static string OrderColorHex => Utils.ColorToHex(OrderColor.Value);
    internal static string ToxicColorHex => Utils.ColorToHex(ToxicColor.Value);
    internal static string ChaosColorHex => Utils.ColorToHex(ChaosColor.Value);
    internal static string LifeColorHex => Utils.ColorToHex(LifeColor.Value);
    internal static string ElectricColorHex => Utils.ColorToHex(ElectricColor.Value);
    internal static string SilverColorHex => Utils.ColorToHex(SilverColor.Value);
    internal static string WhiteColorHex => Utils.ColorToHex(WhiteColor.Value);
    internal static string WaterColorHex => Utils.ColorToHex(WaterColor.Value);
    internal static string OilColorHex => Utils.ColorToHex(OilColor.Value);
    internal static string BloodColorHex => Utils.ColorToHex(BloodColor.Value);
    internal static string SaltColorHex => Utils.ColorToHex(SaltColor.Value);
    internal static string AshColorHex => Utils.ColorToHex(AshColor.Value);
    internal static string AlcoholColorHex => Utils.ColorToHex(AlcoholColor.Value);
    internal static string GoldColorHex => Utils.ColorToHex(GoldColor.Value);
    internal static string GraphiteColorHex => Utils.ColorToHex(GraphiteColor.Value);
    
    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        LogHelper.Log = Log;
        Lang.Init(Assembly.GetExecutingAssembly(), Log);

        Debug = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Logging", false, "debug_logging", order: 1);
        DebugEnabled = Debug.Value;
        Debug.SettingChanged += (_, _) => DebugEnabled = Debug.Value;

        DebugWarningDialog.Register(MyPluginInfo.PLUGIN_NAME, () => DebugEnabled);

        SlowingColor      = LocalizedConfig.Bind(Config, ColorsSection, "Slowing",      new Color(0.478f, 0.235f, 0.043f), "color_slowing");
        AccelerationColor = LocalizedConfig.Bind(Config, ColorsSection, "Acceleration", new Color(0.035f, 0.157f, 0.384f), "color_acceleration");
        HealthColor       = LocalizedConfig.Bind(Config, ColorsSection, "Health",       new Color(0.145f, 0.322f, 0.004f), "color_health");
        DeathColor        = LocalizedConfig.Bind(Config, ColorsSection, "Death",        new Color(0.220f, 0.039f, 0.310f), "color_death");
        OrderColor        = LocalizedConfig.Bind(Config, ColorsSection, "Order",        new Color(0.851f, 0.984f, 0.455f), "color_order");
        ToxicColor        = LocalizedConfig.Bind(Config, ColorsSection, "Toxic",        new Color(0.776f, 0.145f, 0.075f), "color_toxic");
        ChaosColor        = LocalizedConfig.Bind(Config, ColorsSection, "Chaos",        new Color(0.537f, 0.035f, 0.843f), "color_chaos");
        LifeColor         = LocalizedConfig.Bind(Config, ColorsSection, "Life",         new Color(0.647f, 0.435f, 0.004f), "color_life");
        ElectricColor     = LocalizedConfig.Bind(Config, ColorsSection, "Electric",     new Color(0.141f, 1f, 1f),         "color_electric");
        SilverColor       = LocalizedConfig.Bind(Config, ColorsSection, "Silver",       new Color(0.753f, 0.753f, 0.753f), "color_silver");
        WhiteColor        = LocalizedConfig.Bind(Config, ColorsSection, "White",        new Color(1f, 1f, 1f),             "color_white");
        WaterColor        = LocalizedConfig.Bind(Config, ColorsSection, "Water",        new Color(0.004f, 0.004f, 0.404f), "color_water");
        OilColor          = LocalizedConfig.Bind(Config, ColorsSection, "Oil",          new Color(0.157f, 0.157f, 0.157f), "color_oil");
        BloodColor        = LocalizedConfig.Bind(Config, ColorsSection, "Blood",        new Color(0.404f, 0.004f, 0.004f), "color_blood");
        SaltColor         = LocalizedConfig.Bind(Config, ColorsSection, "Salt",         new Color(0.404f, 0.404f, 0.404f), "color_salt");
        AshColor          = LocalizedConfig.Bind(Config, ColorsSection, "Ash",          new Color(0.157f, 0.157f, 0.157f), "color_ash");
        AlcoholColor      = LocalizedConfig.Bind(Config, ColorsSection, "Alcohol",      new Color(0.404f, 0.404f, 0.004f), "color_alcohol");
        GoldColor         = LocalizedConfig.Bind(Config, ColorsSection, "Gold",         new Color(0.831f, 0.686f, 0.216f), "color_gold");
        GraphiteColor     = LocalizedConfig.Bind(Config, ColorsSection, "Graphite",     new Color(0.255f, 0.255f, 0.275f), "color_graphite");

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates", order: 0);

        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

}