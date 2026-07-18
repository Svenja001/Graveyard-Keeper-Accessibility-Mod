namespace LongerDays;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string DayLengthSection = "── Day Length ──";
    private const string AdvancedSection  = "── Advanced ──";
    private const string UpdatesSection   = "── Updates ──";

    internal const float MadnessSeconds = 1350f;
    internal const float EvenLongerSeconds = 1125f;
    internal const float DoubleLengthSeconds = 900f;
    internal const float DefaultIncreaseSeconds = 675f;

    internal static float Seconds;
    internal static TimestampedLogger Log { get; private set; }
    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static bool DebugEnabled;
    private static ConfigEntry<float> DayLength { get; set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        Lang.Init(Assembly.GetExecutingAssembly(), Log);

        Debug = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Logging", false, "debug_logging", order: 100);
        DebugEnabled = Debug.Value;
        Debug.SettingChanged += (_, _) => DebugEnabled = Debug.Value;

        DayLength = LocalizedConfig.Bind(Config, DayLengthSection, "Day Length", 675f, "day_length",
            new AcceptableValueList<float>(675f, 900f, 1125f, 1350f), order: 1,
            extra: a => a.CustomDrawer = LengthSlider);
        Seconds = DayLength.Value;

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates");

        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

    private static void LengthSlider(ConfigEntryBase entry)
    {
        GUILayout.Label($"{Patches.GetTimeMulti()}x", GUILayout.Width(60));
        float[] steps = [675f, 900f, 1125f, 1350f];
        var selectedIndex = Mathf.RoundToInt((DayLength.Value - steps[0]) / (steps[steps.Length - 1] - steps[0]) * (steps.Length - 1));
        var newSelectedIndex = Mathf.RoundToInt(GUILayout.HorizontalSlider(selectedIndex, 0, steps.Length - 1, GUILayout.ExpandWidth(true)));
        if (newSelectedIndex == selectedIndex) return;
        DayLength.Value = steps[newSelectedIndex];
        Seconds = DayLength.Value;
    }

}