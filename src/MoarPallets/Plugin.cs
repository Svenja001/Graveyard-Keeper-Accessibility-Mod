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
    internal static ConfigEntry<bool> WalkThroughPallets { get; private set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        LogHelper.Log = Log;
        Lang.Init(Assembly.GetExecutingAssembly(), Log);

        CheckForUpdates = LocalizedConfig.Bind(Config, "── Updates ──", "Check for Updates", true, "check_for_updates");
        ShowConnectedPopup = LocalizedConfig.Bind(Config, "── Notifications ──", "Show Pallet Connected Popup", true, "show_pallet_connected_popup", order: 100);
        AutoRouteLooseCrates = LocalizedConfig.Bind(Config, "── Pallet ──", "Auto-Route Loose Crates", true, "auto_route_loose_crates", order: 200);
        AutoRouteCarriedCrates = LocalizedConfig.Bind(Config, "── Pallet ──", "Auto-Route Carried Crates", true, "auto_route_carried_crates", order: 199);
        WalkThroughPallets = LocalizedConfig.Bind(Config, "── Pallet ──", "Walk Through Pallets", true, "walk_through_pallets", order: 198);
        WalkThroughPallets.SettingChanged += (_, _) => PalletCollision.RefreshAll();
        Debug = LocalizedConfig.Bind(Config, "── Advanced ──", "Debug Logging", true, "debug_logging", order: 599);

        UpdateChecker.Register(Info, CheckForUpdates);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }
}
