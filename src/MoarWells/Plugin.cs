namespace MoarWells;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal const string BasicWellCraftId = "mf_wood_builddesk::water_well_place";
    internal const string TemplateCraftId = "mf_wood_builddesk::well_pump_place";
    // Includes "remove" in the id so QueueEverything's unsafe-id substring check
    // catches it. Without that, QE turns it into an auto-craft and the build
    // desk's player-held remove stops working.
    internal const string BasicWellRemoveCraftId = ":r:water_well_remove";
    // The beehouse remove takes a few seconds of holding the action button.
    // Cloning from this one (rather than from the garden, which removes
    // instantly) gives the same feel for our wells.
    internal const string RemoveTemplateCraftId = ":r:beehouse_1";
    internal const string BasicWellNameKey = "moarwells_basic_well";

    // Cloned from the vanilla pump craft but with one_time_craft=false and an
    // empty end_script so it can be used many times. The actual upgrade happens
    // in our ProcessFinishedCraft postfix.
    internal const string PumpWellCraftId = "mf_wood_builddesk::well_pump_buildable";
    internal const string PumpWellRemoveCraftId = ":r:well_pump_remove";
    internal const string PumpWellNameKey = "moarwells_pump_well";

    // Build costs. Basic well is half the flitch of vanilla's pump (which costs 8)
    // plus the same in stone. Pump well matches vanilla exactly.
    internal const int BasicWellFlitch = 4;
    internal const int BasicWellStone = 4;
    internal const int PumpWellFlitch = 8;
    internal const int PumpWellNails = 4;
    internal const int PumpWellDetail = 2;

    internal static TimestampedLogger Log { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }
    internal static ConfigEntry<bool> RequireEngineerForPumpWell { get; private set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        LogHelper.Log = Log;

        CheckForUpdates = Config.Bind("── Updates ──", "Check for Updates", true,
            "Show a notice on the main menu when a newer version of this mod is available on NexusMods. Click the notice to open the mod's page.");

        RequireEngineerForPumpWell = Config.Bind("── Unlocks ──", "Require Engineer Tech For Pump Well", true,
            new ConfigDescription(
                "Keep the vanilla Engineer tech requirement before the pump well becomes buildable. Turn this off to unlock the pump well from the start, alongside the basic well.",
                null,
                new ConfigurationManagerAttributes { Order = 0 }));

        Lang.Init(Assembly.GetExecutingAssembly(), Log);
        UpdateChecker.Register(Info, CheckForUpdates);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }
}
