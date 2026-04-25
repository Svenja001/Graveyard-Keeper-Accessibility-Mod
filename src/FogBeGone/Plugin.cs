namespace FogBeGone;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string GeneralSection = "── General ──";
    private const string UpdatesSection = "── Updates ──";

    private static readonly Dictionary<string, string> SectionRenames = new()
    {
        ["01. General"] = GeneralSection,
    };

    private static ManualLogSource Log { get; set; }

    internal static ConfigEntry<bool> RemoveFog { get; private set; }
    internal static ConfigEntry<bool> RemoveWind { get; private set; }
    internal static ConfigEntry<bool> RemoveRain { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    internal static bool RemoveFogCached;
    internal static bool RemoveWindCached;
    internal static bool RemoveRainCached;

    private void Awake()
    {
        Log = Logger;
        ConfigMigration.MigrateRenamedSections(Config, Log, SectionRenames);
        ConfigMigration.MigrateRenamedKeys(Config, Log,
            new ConfigMigration.KeyRename(GeneralSection, "Disable Fog", "Remove Fog"),
            new ConfigMigration.KeyRename(GeneralSection, "Disable Wind", "Remove Wind"),
            new ConfigMigration.KeyRename(GeneralSection, "Disable Rain", "Remove Rain"));
        InitConfiguration();
        UpdateChecker.Register(Info, CheckForUpdates);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

    private void InitConfiguration()
    {
        RemoveFog = Config.Bind(GeneralSection, "Remove Fog", true,
            new ConfigDescription("Remove fog from outdoor and indoor weather.", null,
                new ConfigurationManagerAttributes { Order = 3 }));
        RemoveWind = Config.Bind(GeneralSection, "Remove Wind", false,
            new ConfigDescription("Remove wind weather effects.", null,
                new ConfigurationManagerAttributes { Order = 2 }));
        RemoveRain = Config.Bind(GeneralSection, "Remove Rain", false,
            new ConfigDescription("Remove rain weather effects.", null,
                new ConfigurationManagerAttributes { Order = 1 }));

        RemoveFogCached = RemoveFog.Value;
        RemoveWindCached = RemoveWind.Value;
        RemoveRainCached = RemoveRain.Value;

        RemoveFog.SettingChanged += (_, _) => RemoveFogCached = RemoveFog.Value;
        RemoveWind.SettingChanged += (_, _) => RemoveWindCached = RemoveWind.Value;
        RemoveRain.SettingChanged += (_, _) => RemoveRainCached = RemoveRain.Value;

        CheckForUpdates = Config.Bind(UpdatesSection, "Check for Updates", true,
            new ConfigDescription(
                "Show a notice on the main menu when a newer version of this mod is available on NexusMods. Click the notice to open the mod's page.",
                null,
                new ConfigurationManagerAttributes { Order = 0 }));
    }
}