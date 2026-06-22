namespace Exhaustless;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string AdvancedSection       = "── Advanced ──";
    private const string ToolsSection          = "── Tools ──";
    private const string MeditationSection     = "── Meditation ──";
    private const string SleepSection          = "── Sleep ──";
    private const string GameplaySection       = "── Gameplay ──";
    private const string UnlimitedStatsSection = "── Unlimited Stats ──";
    private const string UpdatesSection        = "── Updates ──";

    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static bool DebugEnabled;

    internal static ConfigEntry<bool> MakeToolsLastLonger { get;private set; }
    internal static ConfigEntry<bool> SpendHalfGratitude { get; private set; }
    internal static ConfigEntry<bool> AutoEquipNewTool { get; private set; }
    internal static ConfigEntry<bool> SpeedUpSleep { get; private set; }
    internal static ConfigEntry<bool> SleepAnytime { get; private set; }
    internal static ConfigEntry<bool> AutoWakeFromMeditationWhenStatsFull { get; private set; }
    internal static ConfigEntry<bool> SpendHalfSanity { get; private set; }
    internal static ConfigEntry<bool> SpeedUpMeditation { get; private set; }
    internal static ConfigEntry<bool> SpendHalfEnergy { get; private set; }
    internal static ConfigEntry<bool> UnlimitedEnergy { get; private set; }
    internal static ConfigEntry<bool> UnlimitedGratitude { get; private set; }
    internal static ConfigEntry<bool> UnlimitedHealth { get; private set; }
    internal static ConfigEntry<bool> UnlimitedSanity { get; private set; }
    internal static ConfigEntry<int> EnergySpendBeforeSleepDebuff { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }
    private static TimestampedLogger Log { get; set; }

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
                theirGuid: "codesprint.energy_edit",
                theirName: "Energy Edit",
                feature: Lang.Get("Conflict.EnergyEdit.Feature"),
                severity: ConflictSeverity.Race,
                note: Lang.Get("Conflict.EnergyEdit.Note")),
        });
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

    private void InitConfiguration()
    {
        Debug = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Logging", false, "debug_logging", order: 50);
        DebugEnabled = Debug.Value;
        Debug.SettingChanged += (_, _) => DebugEnabled = Debug.Value;

        AutoEquipNewTool = LocalizedConfig.Bind(Config, ToolsSection, "Auto Equip New Tool", true, "auto_equip_new_tool", order: 49);
        MakeToolsLastLonger = LocalizedConfig.Bind(Config, ToolsSection, "Make Tools Last Longer", true, "make_tools_last_longer", order: 48);

        AutoWakeFromMeditationWhenStatsFull = LocalizedConfig.Bind(Config, MeditationSection, "Auto Wake From Meditation When Stats Full", true, "auto_wake_from_meditation_when_stats_full", order: 47);
        SpeedUpMeditation = LocalizedConfig.Bind(Config, MeditationSection, "Speed Up Meditation", true, "speed_up_meditation", order: 46);

        SleepAnytime = LocalizedConfig.Bind(Config, SleepSection, "Sleep Anytime", false, "sleep_anytime", order: 46);
        EnergySpendBeforeSleepDebuff = LocalizedConfig.Bind(Config, SleepSection, "Energy Spend Before Sleep Debuff", 1200, "energy_spend_before_sleep_debuff", new AcceptableValueRange<int>(350, 50000), order: 45);
        SpeedUpSleep = LocalizedConfig.Bind(Config, SleepSection, "Speed Up Sleep", true, "speed_up_sleep", order: 44);

        SpendHalfEnergy = LocalizedConfig.Bind(Config, GameplaySection, "Spend Half Energy", true, "spend_half_energy", order: 43);
        SpendHalfEnergy.SettingChanged += (_, _) =>
        {
            if (SpendHalfEnergy.Value)
                UnlimitedEnergy.Value = false;
        };

        SpendHalfGratitude = LocalizedConfig.Bind(Config, GameplaySection, "Spend Half Gratitude", true, "spend_half_gratitude", order: 42);
        SpendHalfGratitude.SettingChanged += (_, _) =>
        {
            if (SpendHalfGratitude.Value)
                UnlimitedGratitude.Value = false;
        };

        SpendHalfSanity = LocalizedConfig.Bind(Config, GameplaySection, "Spend Half Sanity", true, "spend_half_sanity", order: 41);
        SpendHalfSanity.SettingChanged += (_, _) =>
        {
            if (SpendHalfSanity.Value)
                UnlimitedSanity.Value = false;
        };

        UnlimitedEnergy = LocalizedConfig.Bind(Config, UnlimitedStatsSection, "Unlimited Energy", false, "unlimited_energy", order: 40);
        UnlimitedEnergy.SettingChanged += (_, _) =>
        {
            if (UnlimitedEnergy.Value)
                SpendHalfEnergy.Value = false;
        };
        UnlimitedGratitude = LocalizedConfig.Bind(Config, UnlimitedStatsSection, "Unlimited Gratitude", false, "unlimited_gratitude", order: 39);
        UnlimitedGratitude.SettingChanged += (_, _) =>
        {
            if (UnlimitedGratitude.Value)
                SpendHalfGratitude.Value = false;
        };
        UnlimitedSanity = LocalizedConfig.Bind(Config, UnlimitedStatsSection, "Unlimited Sanity", false, "unlimited_sanity", order: 38);
        UnlimitedSanity.SettingChanged += (_, _) =>
        {
            if (UnlimitedSanity.Value)
                SpendHalfSanity.Value = false;
        };

        UnlimitedHealth = LocalizedConfig.Bind(Config, UnlimitedStatsSection, "Unlimited Health", false, "unlimited_health", order: 37);

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates", order: 36);
    }

}