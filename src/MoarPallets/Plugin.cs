namespace MoarPallets;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal const string PalletCraftId = "mf_wood_builddesk::box_pallet_buildable";
    internal const string TemplatePalletCraftId = "storage_builddesk:p:box_pallet";
    // "remove" in the id so QueueEverything's unsafe-id check leaves it alone.
    internal const string PalletRemoveCraftId = ":r:box_pallet_remove";
    // Beehouse remove takes a few seconds of holding - same feel we want for the pallet.
    internal const string RemoveTemplateCraftId = ":r:beehouse_1";
    internal const string PalletNameKey = "moarpallets_pallet";
    internal const string PalletMainBuilderId = "mf_wood_builddesk";
    internal const string PalletCellarBuilderId = "cellar_builddesk";

    internal const int PalletFlitch = 6;
    internal const int PalletNails = 4;

    internal static TimestampedLogger Log { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }
    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static ConfigEntry<bool> ShowConnectedPopup { get; private set; }
    internal static ConfigEntry<bool> AutoRouteLooseCrates { get; private set; }
    internal static ConfigEntry<bool> AutoRouteCarriedCrates { get; private set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        LogHelper.Log = Log;

        CheckForUpdates = Config.Bind("── Updates ──", "Check for Updates", true,
            "Show a notice on the main menu when a newer version of this mod is available on NexusMods. Click the notice to open the mod's page.");

        ShowConnectedPopup = Config.Bind("── Notifications ──", "Show Pallet Connected Popup", true,
            new ConfigDescription("Show a small 'Linked to elevator' message above any pallet you build in the cellar, confirming the elevator and the porter will use it. Turn off for a quieter cellar.", null,
                new ConfigurationManagerAttributes { Order = 100 }));

        AutoRouteLooseCrates = Config.Bind("── Pallet ──", "Auto-Route Loose Crates", true,
            new ConfigDescription("On game load, slide any loose crate lying around onto the nearest pallet with space. Crates left over when every pallet is full get parked at the cellar elevator instead.", null,
                new ConfigurationManagerAttributes { Order = 200 }));

        AutoRouteCarriedCrates = Config.Bind("── Pallet ──", "Auto-Route Carried Crates", true,
            new ConfigDescription("When you drop a crate you're carrying, send it straight to the nearest pallet with space instead of leaving it on the floor. Anything left over when every pallet is full ends up at the cellar elevator.", null,
                new ConfigurationManagerAttributes { Order = 199 }));

        Debug = Config.Bind("── Advanced ──", "Debug Logging", true,
            new ConfigDescription("Write verbose pallet-and-zone diagnostics to the BepInEx console. Useful while figuring out why a pallet isn't getting filled. Leave off for normal play.", null,
                new ConfigurationManagerAttributes { Order = 599 }));

        Lang.Init(Assembly.GetExecutingAssembly(), Log);
        UpdateChecker.Register(Info, CheckForUpdates);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }
}
