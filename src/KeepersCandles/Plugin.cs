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
    internal static ConfigEntry<bool> CandlesBurnDown { get; private set; }
    internal static ConfigEntry<bool> IncenseBurnsDown { get; private set; }
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

        // Both are bound before either handler, because a change re-runs the pass that reads both.
        CandlesBurnDown = LocalizedConfig.Bind(Config, CandlesSection, "Candles Burn Down", false, "candles_burn_down", order: 98);
        IncenseBurnsDown = LocalizedConfig.Bind(Config, CandlesSection, "Incense Burns Down", false, "incense_burns_down", order: 97);
        CandlesBurnDown.SettingChanged += (_, _) => Patches.OnGameBalanceLoaded();
        IncenseBurnsDown.SettingChanged += (_, _) => Patches.OnGameBalanceLoaded();
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

    // Whether this candle holder or incense burner stays lit forever with the current settings.
    internal static bool KeepsBurning(string id)
    {
        if (!ShouldProcess(id)) return false;

        return id.Contains(Candelabrum) ? !CandlesBurnDown.Value : !IncenseBurnsDown.Value;
    }

    internal static bool MatchesKeyword(string id, string keyword)
    {
        return !id.Contains(Souls) && id.Contains(keyword);
    }

    // A burner's own id says whether it's lit.
    internal static bool IsLit(string id)
    {
        // c_obj_incense_N is the lit burner; the _place version is the empty build state.
        if (MatchesKeyword(id, Incense)) return !id.EndsWith("_place");
        if (!MatchesKeyword(id, Candelabrum)) return false;

        // candelabrum_N_q has two underscores after the keyword; the bare candelabrum_N has one.
        var postfix = id.Split([Candelabrum], StringSplitOptions.None).Last();
        return postfix.Count(c => c == '_') >= 2;
    }

    internal static string GetUnlitReplacement(WorldGameObject wgo)
    {
        string unlit;

        // Incense burner: lit "c_obj_incense_2" turns back into empty "c_obj_incense_2_place".
        // If it already ends in _place it's empty, so there's nothing to extinguish.
        if (MatchesKeyword(wgo.obj_id, Incense))
        {
            unlit = wgo.obj_id.EndsWith("_place") ? string.Empty : wgo.obj_id + "_place";
        }
        else
        {
            // Candelabrum: lit "wall_candelabrum_2_1" drops its candle-count suffix back to "wall_candelabrum_2".
            var cut = wgo.obj_id.LastIndexOf('_');
            unlit = cut > 0 ? wgo.obj_id.Substring(0, cut) : string.Empty;
        }

        if (string.IsNullOrWhiteSpace(unlit)) return string.Empty;

        // Swapping in an id the game has no object for breaks the object, so check it exists first.
        return GameBalance.me.GetDataOrNull<ObjectDefinition>(unlit) != null ? unlit : string.Empty;
    }

    internal static List<WorldGameObject> GetCandles()  => GetLitBurners(Candelabrum);
    internal static List<WorldGameObject> GetIncenses() => GetLitBurners(Incense);

    private static List<WorldGameObject> GetLitBurners(string keyword)
    {
        // Outside any zone there's nothing nearby to point at. Searching the whole world found
        // candles in the church from across the map and aimed the arrow off the edge of it.
        var zone = MainGame.me.player.GetMyWorldZone();
        if (!zone) return [];

        // A burner marked for demolition has a craft running, so it would otherwise look lit.
        // Leave those to the build desk.
        return zone.GetZoneWGOs().Where(wgo => MatchesKeyword(wgo.obj_id, keyword) && IsLit(wgo.obj_id) && !wgo.is_removing).ToList();
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
