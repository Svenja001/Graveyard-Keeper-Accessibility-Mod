namespace WheresMaVeggies;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string AdvancedSection = "── Advanced ──";
    private const string HarvestSection  = "── Harvest ──";
    private const string UpdatesSection  = "── Updates ──";

    private static readonly Dictionary<string, string> SectionRenames = new()
    {
        ["── 1. Advanced ──"] = AdvancedSection,
        ["── 2. Harvest ──"]  = HarvestSection,
        ["── 3. Updates ──"]  = UpdatesSection,
    };

    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static bool DebugEnabled;

    internal static ConfigEntry<bool> RequireFarmerPerk { get; private set; }
    internal static ConfigEntry<int> CascadeRadius { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    internal static TimestampedLogger Log { get; private set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        LogHelper.Log = Log;
        MigrateRenamedSections();

        Debug = Config.Bind(AdvancedSection, "Debug Logging", false,
            new ConfigDescription("Write detailed diagnostic info to the BepInEx log while you play. Turn this on before reporting a bug so the log has the context I need to help.", null,
                new ConfigurationManagerAttributes {Order = 100}));
        DebugEnabled = Debug.Value;
        Debug.SettingChanged += (_, _) => DebugEnabled = Debug.Value;

        RequireFarmerPerk = Config.Bind(HarvestSection, "Require Farmer Perk", true,
            new ConfigDescription("On: nearby garden plots only mass-harvest after you've unlocked the Farmer perk in the tech tree. This matches the mod's original behaviour. Off: every harvest immediately reaps neighbouring plots of the same crop, even before the perk is unlocked - useful if you want the convenience from day one.", null,
                new ConfigurationManagerAttributes {Order = 100}));

        CascadeRadius = Config.Bind(HarvestSection, "Cascade Radius (tiles)", 1,
            new ConfigDescription("How far the harvest cascade reaches from the bed you clicked, measured in game tiles. 1 catches the 8 beds touching it (3x3 = 9 beds total). 2 catches a 5x5 (25 beds). Larger values pull in more neighbours but check more world objects per harvest, so leave this low unless you have very large plots.",
                new AcceptableValueRange<int>(1, 5),
                new ConfigurationManagerAttributes {Order = 90}));

        CheckForUpdates = Config.Bind(UpdatesSection, "Check for Updates", true,
            new ConfigDescription(
                "Show a notice on the main menu when a newer version of this mod is available on NexusMods. Click the notice to open the mod's page.",
                null,
                new ConfigurationManagerAttributes {Order = 100}));

        Lang.Init(Assembly.GetExecutingAssembly(), Log);
        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        DebugWarningDialog.Register(MyPluginInfo.PLUGIN_NAME, () => DebugEnabled);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
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

    private void MigrateRenamedSections()
    {
        var path = Config.ConfigFilePath;
        if (!File.Exists(path)) return;

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[Migration] Could not read {path} for section rename: {ex.Message}");
            return;
        }

        var renamed = 0;
        foreach (var kv in SectionRenames)
        {
            var oldHeader = $"[{kv.Key}]";
            var newHeader = $"[{kv.Value}]";
            if (!content.Contains(oldHeader)) continue;
            content = content.Replace(oldHeader, newHeader);
            renamed++;
        }
        if (renamed == 0) return;

        try
        {
            File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[Migration] Could not write {path} after section rename: {ex.Message}");
            return;
        }

        Log.LogInfo($"[Migration] Renamed {renamed} legacy config section header(s) to the '── Name ──' style. Existing user values preserved.");
        Config.Reload();
    }
}
