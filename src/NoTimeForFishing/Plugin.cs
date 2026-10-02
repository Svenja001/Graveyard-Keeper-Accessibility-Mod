namespace NoTimeForFishing;

public enum RareFishCatchRate
{
    Vanilla,
    SlightlyIncreased,
    Increased,
    GreatlyIncreased,
    VeryCommon,
}

public enum FishingMode
{
    SkipEverything,
    AutoHookOnly,
}

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static TimestampedLogger Log { get; private set; }
    internal static ConfigEntry<FishingMode> Mode { get; private set; }
    internal static ConfigEntry<RareFishCatchRate> RareFishRate { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        Lang.Init(Assembly.GetExecutingAssembly(), Log);

        Mode = LocalizedConfig.Bind(Config, "── General ──", "Mode", FishingMode.SkipEverything, "mode", order: 1);
        RareFishRate = LocalizedConfig.Bind(Config, "── Catch Rates ──", "Rare Fish Catch Rate", RareFishCatchRate.Vanilla, "rare_fish_catch_rate", order: 1);
        CheckForUpdates = LocalizedConfig.Bind(Config, "── Updates ──", "Check for Updates", true, "check_for_updates");
        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        Log.LogInfo($"Plugin loaded. Mode: {Mode.Value}. Rare Fish Catch Rate: {RareFishRate.Value}.");
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

}
