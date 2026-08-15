namespace EconomyReloaded;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string EconomySection = "── Economy ──";
    private const string UpdatesSection = "── Updates ──";

    internal static TimestampedLogger Log { get; private set; }

    internal static ConfigEntry<bool> DynamicBuyPricing { get; private set; }
    internal static ConfigEntry<float> BuyPriceMultiplier { get; private set; }
    internal static ConfigEntry<bool> DynamicSellPricing { get; private set; }
    internal static ConfigEntry<float> SellPriceMultiplier { get; private set; }
    internal static ConfigEntry<bool> AggressiveVendorRestock { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        Lang.Init(Assembly.GetExecutingAssembly(), Log);
        InitConfiguration();
        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

    private void InitConfiguration()
    {
        DynamicBuyPricing = LocalizedConfig.Bind(Config, EconomySection, "Dynamic Buy Pricing", true, "dynamic_buy_pricing", order: 4);
        BuyPriceMultiplier = LocalizedConfig.Bind(Config, EconomySection, "Buy Price Multiplier", 1.0f, "buy_price_multiplier", new AcceptableValueRange<float>(0.1f, 5.0f), order: 3);
        DynamicSellPricing = LocalizedConfig.Bind(Config, EconomySection, "Dynamic Sell Pricing", true, "dynamic_sell_pricing", order: 2);
        SellPriceMultiplier = LocalizedConfig.Bind(Config, EconomySection, "Sell Price Multiplier", 0.75f, "sell_price_multiplier", new AcceptableValueRange<float>(0.1f, 5.0f), order: 1);
        AggressiveVendorRestock = LocalizedConfig.Bind(Config, EconomySection, "Aggressive Vendor Restock", false, "aggressive_vendor_restock", order: 0);
        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates");
    }
}
