namespace KeepersCandles;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal const string Souls = "souls";
    internal const string Candelabrum = "candelabrum";
    internal const string Incense = "incense";
    internal const string Column = "column";
    internal const string Church = "CHURCH";

    private const string AdvancedSection = "── Advanced ──";
    private const string CandlesSection  = "── Candles & Incenses ──";
    private const string ChurchSection   = "── Church ──";
    private const string ControlsSection = "── Controls ──";
    private const string UpdatesSection  = "── Updates ──";

    internal static readonly List<GameObject> ChurchColumnsList = [];

    internal static TimestampedLogger Log { get; private set; }
    internal static bool DebugEnabled;

    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static ConfigEntry<float> ExtinguishDistance { get; private set; }
    internal static ConfigEntry<bool> DirectionalArrow { get; private set; }
    internal static ConfigEntry<bool> ChurchColumns { get; private set; }
    internal static ConfigEntry<KeyboardShortcut> ExtinguishCandleKeyBind { get; private set; }
    internal static ConfigEntry<string> ExtinguishCandleControllerButton { get; private set; }
    internal static ConfigEntry<KeyboardShortcut> ExtinguishIncenseKeyBind { get; private set; }
    internal static ConfigEntry<string> ExtinguishIncenseControllerButton { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    internal static Vector2 PlayerPosition => MainGame.me.player.grid_pos;

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        LogHelper.Log = Log;
        Lang.Init(Assembly.GetExecutingAssembly(), Log);
        InitConfiguration();
        SceneManager.sceneLoaded += (_, _) => Patches.OnGameBalanceLoaded();
        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        DebugWarningDialog.Register(MyPluginInfo.PLUGIN_NAME, () => DebugEnabled);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

    private void InitConfiguration()
    {
        Debug = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Logging", false, "debug_logging", order: 100);
        DebugEnabled = Debug.Value;
        Debug.SettingChanged += (_, _) => DebugEnabled = Debug.Value;

        ExtinguishDistance = LocalizedConfig.Bind(Config, CandlesSection, "Extinguish Distance", 1f, "extinguish_distance", new AcceptableValueRange<float>(1, 5), order: 100);
        ExtinguishDistance.SettingChanged += (_, _) =>
        {
            ExtinguishDistance.Value = Mathf.Round(ExtinguishDistance.Value * 4) / 4;
        };

        DirectionalArrow = LocalizedConfig.Bind(Config, CandlesSection, "Directional Arrow", true, "directional_arrow", order: 99);
        DirectionalArrow.SettingChanged += (_, _) => Patches.ResetArrow();

        ChurchColumns = LocalizedConfig.Bind(Config, ChurchSection, "Church Columns", true, "church_columns", order: 100);
        ChurchColumns.SettingChanged += (_, _) => Patches.ChurchColumnsToggle();

        ExtinguishCandleKeyBind = LocalizedConfig.Bind(Config, ControlsSection, "Extinguish Candle Keybind", new KeyboardShortcut(KeyCode.C), "extinguish_candle_keybind", order: 100);
        ExtinguishCandleControllerButton = LocalizedConfig.Bind(Config, ControlsSection, "Extinguish Candle Controller Button", Enum.GetName(typeof(GamePadButton), GamePadButton.DUp), "extinguish_candle_controller_button", new AcceptableValueList<string>(Enum.GetNames(typeof(GamePadButton))), order: 99);
        ExtinguishIncenseKeyBind = LocalizedConfig.Bind(Config, ControlsSection, "Extinguish Incense Keybind", new KeyboardShortcut(KeyCode.None), "extinguish_incense_keybind", order: 98);
        ExtinguishIncenseControllerButton = LocalizedConfig.Bind(Config, ControlsSection, "Extinguish Incense Controller Button", Enum.GetName(typeof(GamePadButton), GamePadButton.None), "extinguish_incense_controller_button", new AcceptableValueList<string>(Enum.GetNames(typeof(GamePadButton))), order: 97);

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates", order: 100);
    }

    internal static bool CanFindCandles()
    {
        return MainGame.game_started &&
               !MainGame.me.player.is_dead &&
               !MainGame.me.player.IsDisabled() &&
               !MainGame.paused &&
               BaseGUI.all_guis_closed;
    }

    internal static bool ShouldProcess(string id)
    {
        return !id.Contains(Souls) && (id.Contains(Candelabrum) || id.Contains(Incense));
    }

    internal static bool MatchesKeyword(string id, string keyword)
    {
        return !id.Contains(Souls) && id.Contains(keyword);
    }

    internal static string GetUnlitReplacement(WorldGameObject wgo)
    {
        // Incense burner: lit "c_obj_incense_2" turns back into empty "c_obj_incense_2_place".
        // If it already ends in _place it's empty, so there's nothing to extinguish.
        if (MatchesKeyword(wgo.obj_id, Incense))
        {
            return wgo.obj_id.EndsWith("_place") ? string.Empty : wgo.obj_id + "_place";
        }

        // Candelabrum: lit "wall_candelabrum_2_1" drops its candle-count suffix back to "wall_candelabrum_2".
        var cut = wgo.obj_id.LastIndexOf('_');
        return cut > 0 ? wgo.obj_id.Substring(0, cut) : string.Empty;
    }

    internal static List<WorldGameObject> GetCandles()  => GetLitBurners(Candelabrum);
    internal static List<WorldGameObject> GetIncenses() => GetLitBurners(Incense);

    private static List<WorldGameObject> GetLitBurners(string keyword)
    {
        var zone = MainGame.me.player.GetMyWorldZone();

        var all = zone
            ? zone.GetZoneWGOs().Where(wgo => MatchesKeyword(wgo.obj_id, keyword) || MatchesKeyword(wgo.obj_def.id, keyword)).ToList()
            : WorldMap._objs.Where(wgo => MatchesKeyword(wgo.obj_id, keyword) || MatchesKeyword(wgo.obj_def.id, keyword)).ToList();

        return all.Where(wgo => wgo.components.craft.is_crafting).ToList();
    }

    internal static string GetPath(Transform transform)
    {
        var path = transform.name;
        while (transform.parent)
        {
            transform = transform.parent;
            path = $"{transform.name}/{path}";
        }
        return path;
    }
}
