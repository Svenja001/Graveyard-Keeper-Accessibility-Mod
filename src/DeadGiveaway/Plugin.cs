namespace DeadGiveaway;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string GeneralSection    = "── General ──";
    private const string ControlsSection   = "── Controls ──";
    private const string ControllerSection = "── Controller ──";
    private const string AdvancedSection   = "── Advanced ──";
    private const string UpdatesSection    = "── Updates ──";

    internal static TimestampedLogger Log { get; private set; }
    internal static bool DebugEnabled;

    internal static ConfigEntry<bool> StartEnabled { get; private set; }
    internal static ConfigEntry<bool> ShowPercentage { get; private set; }
    internal static ConfigEntry<bool> ShowSkulls { get; private set; }
    internal static ConfigEntry<bool> ShowSpeed { get; private set; }
    internal static ConfigEntry<bool> ShowJob { get; private set; }
    internal static ConfigEntry<bool> ColourByEfficiency { get; private set; }
    internal static ConfigEntry<float> TextSize { get; private set; }
    internal static ConfigEntry<float> VerticalOffset { get; private set; }
    internal static ConfigEntry<float> HorizontalOffset { get; private set; }
    internal static ConfigEntry<KeyboardShortcut> ToggleKeybind { get; private set; }
    internal static ConfigEntry<string> ToggleControllerButton { get; private set; }
    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    // Configured controller button, parsed to the game's enum once instead of every frame.
    internal static GamePadButton ToggleButton;

    // Live on/off state. Starts from StartEnabled, flipped by the key or controller button.
    internal static bool OverlayVisible;

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        Lang.Init(Assembly.GetExecutingAssembly(), Log);
        InitConfiguration();

        OverlayVisible = StartEnabled.Value;

        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        DebugWarningDialog.Register(MyPluginInfo.PLUGIN_NAME, () => DebugEnabled);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);

        var host = new GameObject("~DeadGiveawayOverlay");
        DontDestroyOnLoad(host);
        host.AddComponent<ZombieEfficiencyOverlay>();
    }

    private void InitConfiguration()
    {
        StartEnabled = LocalizedConfig.Bind(Config, GeneralSection, "Start Enabled", true, "start_enabled", order: 100);

        ShowPercentage = LocalizedConfig.Bind(Config, GeneralSection, "Show Percentage", true, "show_percentage", order: 99);

        ShowSkulls = LocalizedConfig.Bind(Config, GeneralSection, "Show Skulls", false, "show_skulls", order: 98);

        ShowSpeed = LocalizedConfig.Bind(Config, GeneralSection, "Show Speed", false, "show_speed", order: 97);

        ShowJob = LocalizedConfig.Bind(Config, GeneralSection, "Show Job", false, "show_job", order: 96);

        ColourByEfficiency = LocalizedConfig.Bind(Config, GeneralSection, "Colour by Efficiency", true, "colour_by_efficiency", order: 95);

        TextSize = LocalizedConfig.Bind(Config, GeneralSection, "Text Size", 1f, "text_size", new AcceptableValueRange<float>(0.5f, 3f), order: 94);

        VerticalOffset = LocalizedConfig.Bind(Config, GeneralSection, "Vertical Offset", 30f, "vertical_offset", new AcceptableValueRange<float>(0f, 120f), order: 93);

        HorizontalOffset = LocalizedConfig.Bind(Config, GeneralSection, "Horizontal Offset", 0f, "horizontal_offset", new AcceptableValueRange<float>(-120f, 120f), order: 92);

        ToggleKeybind = LocalizedConfig.Bind(Config, ControlsSection, "Toggle Overlay Keybind", new KeyboardShortcut(KeyCode.B), "toggle_overlay_keybind", order: 100);

        ToggleControllerButton = LocalizedConfig.Bind(Config, ControllerSection, "Toggle Overlay Controller Button",
            Enum.GetName(typeof(GamePadButton), GamePadButton.None), "toggle_overlay_controller_button",
            new AcceptableValueList<string>(Enum.GetNames(typeof(GamePadButton))), order: 100);
        ToggleButton = ParseButton(ToggleControllerButton.Value);
        ToggleControllerButton.SettingChanged += (_, _) => ToggleButton = ParseButton(ToggleControllerButton.Value);

        Debug = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Logging", false, "debug_logging", order: 100);
        DebugEnabled = Debug.Value;
        Debug.SettingChanged += (_, _) => DebugEnabled = Debug.Value;

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates", order: 100);
    }

    // Only react to the toggle while the player is actually in control.
    internal static bool CanToggle()
    {
        return MainGame.game_started &&
               !MainGame.me.player.is_dead &&
               !MainGame.me.player.IsDisabled() &&
               !MainGame.paused &&
               BaseGUI.all_guis_closed;
    }

    private static GamePadButton ParseButton(string name)
    {
        return Enum.TryParse(name, out GamePadButton button) ? button : GamePadButton.None;
    }
}
