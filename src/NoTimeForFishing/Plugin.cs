namespace NoTimeForFishing;

public enum RareFishCatchRate
{
    Vanilla,
    SlightlyIncreased,
    Increased,
    GreatlyIncreased,
    VeryCommon,
}

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static TimestampedLogger Log { get; private set; }
    internal static ConfigEntry<RareFishCatchRate> RareFishRate { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        RareFishRate = Config.Bind("── Catch Rates ──", "Rare Fish Catch Rate", RareFishCatchRate.Vanilla,
            new ConfigDescription(
                "How often you reel in rare fish - the gold fish, gold-tier salmon, gold-tier sturgeon, and gold-tier crucian. " +
                "Percentages assume the best rod, gem lure, distance 3, and daytime fishing; other rods or spots will feel similar.\n\n" +
                "• Vanilla - about 1 rare in 20 casts (around 5%, the default game rate).\n" +
                "• Slightly Increased - about 1 in 9 casts (around 11%).\n" +
                "• Increased - about 1 in 5 casts (around 20%).\n" +
                "• Greatly Increased - about 1 in 3 casts (around 33%).\n" +
                "• Very Common - about every other cast (around 50%).",
                null,
                new ConfigurationManagerAttributes { Order = 1 }));
        CheckForUpdates = Config.Bind("── Updates ──", "Check for Updates", true,
            "Show a notice on the main menu when a newer version of this mod is available on NexusMods. Click the notice to open the mod's page.");
        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        Log.LogInfo($"Plugin loaded. Rare Fish Catch Rate: {RareFishRate.Value}.");
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

}
