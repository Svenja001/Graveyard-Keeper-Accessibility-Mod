namespace AlchemyResearchRedux;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string ControlsSection   = "── Controls ──";
    private const string ControllerSection = "── Controller ──";
    private const string AdvancedSection   = "── Advanced ──";
    private const string UpdatesSection    = "── Updates ──";

    internal static TimestampedLogger Log { get; private set; }
    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static bool DebugEnabled;
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }
    internal static ConfigEntry<KeyboardShortcut> RefillLastMixKey { get; private set; }
    internal static ConfigEntry<bool> AutoRefillOnOpen { get; private set; }
    internal static ConfigEntry<string> RefillControllerButton { get; private set; }
    internal static GamePadButton RefillButton;

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        LogHelper.Log = Log;
        Lang.Init(Assembly.GetExecutingAssembly(), Log);

        Debug = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Logging", false, "debug_logging", order: 1);
        DebugEnabled = Debug.Value;
        Debug.SettingChanged += (_, _) => DebugEnabled = Debug.Value;

        DebugWarningDialog.Register(MyPluginInfo.PLUGIN_NAME, () => DebugEnabled);

        RefillLastMixKey = LocalizedConfig.Bind(Config, ControlsSection, "Refill Last Mix", new KeyboardShortcut(KeyCode.R), "refill_last_mix");
        AutoRefillOnOpen = LocalizedConfig.Bind(Config, ControlsSection, "Auto-Refill On Open", false, "auto_refill_on_open");

        RefillControllerButton = LocalizedConfig.Bind(Config, ControllerSection, "Refill Last Mix Controller Button",
            Enum.GetName(typeof(GamePadButton), GamePadButton.None), "refill_controller_button",
            new AcceptableValueList<string>(Enum.GetNames(typeof(GamePadButton))));
        RefillButton = ParseButton(RefillControllerButton.Value);
        RefillControllerButton.SettingChanged += (_, _) => RefillButton = ParseButton(RefillControllerButton.Value);

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates");

        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

    private static GamePadButton ParseButton(string name)
    {
        return Enum.TryParse(name, out GamePadButton button) ? button : GamePadButton.None;
    }
}