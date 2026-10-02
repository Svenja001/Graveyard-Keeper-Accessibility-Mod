namespace ThoughtfulReminders;

public enum HarvestReminderTiming
{
    WhenReady,
    NextMorning
}

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string AdvancedSection  = "── Advanced ──";
    private const string RemindersSection = "── Reminders ──";
    private const string UpdatesSection   = "── Updates ──";

    internal static ConfigEntry<bool> Debug { get; private set; }
    internal static bool DebugEnabled;

    internal static ConfigEntry<bool> EnableEventMessages { get; private set; }
    internal static ConfigEntry<bool> DaysOnlyConfig { get; private set; }
    internal static ConfigEntry<bool> SpeechBubblesConfig { get; private set; }
    internal static ConfigEntry<bool> ConfessionReminders { get; private set; }
    internal static ConfigEntry<bool> HarvestReminders { get; private set; }
    internal static ConfigEntry<HarvestReminderTiming> HarvestTiming { get; private set; }
    internal static ConfigEntry<float> WakeUpDelay { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    internal static TimestampedLogger Log { get; private set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        LogHelper.Log = Log;
        Lang.Init(Assembly.GetExecutingAssembly(), Log);
        InitConfiguration();
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

        EnableEventMessages = LocalizedConfig.Bind(Config, RemindersSection, "Event Messages", true, "event_messages", order: 100);
        DaysOnlyConfig = LocalizedConfig.Bind(Config, RemindersSection, "Days Only", false, "days_only", order: 99);
        SpeechBubblesConfig = LocalizedConfig.Bind(Config, RemindersSection, "Speech Bubbles", true, "speech_bubbles", order: 90);
        ConfessionReminders = LocalizedConfig.Bind(Config, RemindersSection, "Confession Reminders", true, "confession_reminders", order: 88);
        HarvestReminders = LocalizedConfig.Bind(Config, RemindersSection, "Harvest Reminders", true, "harvest_reminders", order: 86);
        HarvestTiming = LocalizedConfig.Bind(Config, RemindersSection, "Harvest Reminder Timing", HarvestReminderTiming.WhenReady, "harvest_timing", order: 84);
        WakeUpDelay = LocalizedConfig.Bind(Config, RemindersSection, "Wake-Up Delay", 2f, "wake_up_delay", new AcceptableValueRange<float>(0f, 10f), order: 80);

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates", order: 100);
    }
}
