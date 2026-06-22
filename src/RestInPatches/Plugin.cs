namespace RestInPatches;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string ApplicationSection = "── Application ──";
    private const string MovementSection    = "── Movement ──";
    private const string FootprintsSection  = "── Footprints ──";
    private const string UpdatesSection     = "── Updates ──";

    internal static TimestampedLogger Log { get; set; }
    internal static Sprite ArrowLeftSprite { get; private set; }
    internal static Sprite ArrowUpSprite { get; private set; }
    internal static Sprite ArrowDownSprite { get; private set; }
    private static ConfigEntry<bool> KeepRunningInBackground { get; set; }
    private static ConfigEntry<bool> MuteWhenUnfocused { get; set; }
    internal static ConfigEntry<bool> SmoothPlayerMovement { get; private set; }
    internal static ConfigEntry<int> MaxFootprints { get; private set; }
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        Lang.Init(Assembly.GetExecutingAssembly(), Log);
        InitConfiguration();
        ArrowLeftSprite = LoadEmbeddedSprite("RestInPatches.Resources.ui_btn_arrow_left.png", "ui_btn_arrow_left");
        if (ArrowLeftSprite == null)
        {
            Log.LogError("Failed to load embedded left arrow sprite; amount-button arrow fix will be inactive.");
        }

        ArrowUpSprite = LoadEmbeddedSprite("RestInPatches.Resources.ui_btn_arrow_up.png", "ui_btn_arrow_up");
        if (ArrowUpSprite == null)
        {
            Log.LogError("Failed to load embedded up arrow sprite; expanded details-toggle arrow fix will be inactive.");
        }

        ArrowDownSprite = LoadEmbeddedSprite("RestInPatches.Resources.ui_btn_arrow_dn.png", "ui_btn_arrow_dn");
        if (ArrowDownSprite == null)
        {
            Log.LogError("Failed to load embedded down arrow sprite; collapsed details-toggle arrow fix will be inactive.");
        }

        SceneManager.sceneLoaded += OnSceneLoaded;
        Application.focusChanged += OnFocusChanged;

        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        ConflictWarningRegistry.Register(MyPluginInfo.PLUGIN_NAME, () => new[]
        {
            new ConflictEntry(
                theirGuid: "Aze.GYK.PlayerJudderFix",
                theirName: "Player Judder Fix",
                feature: Lang.Get("Conflict.PlayerJudderFix.Feature"),
                severity: ConflictSeverity.Hint,
                note: Lang.Get("Conflict.PlayerJudderFix.Note")),
            new ConflictEntry(
                theirGuid: "Aze.GYK.FootprintPerformance",
                theirName: "Footprint Performance",
                feature: Lang.Get("Conflict.FootprintPerformance.Feature"),
                severity: ConflictSeverity.Hint,
                note: Lang.Get("Conflict.FootprintPerformance.Note")),
        });
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

    private void InitConfiguration()
    {
        KeepRunningInBackground = LocalizedConfig.Bind(Config, ApplicationSection, "Keep Running In Background", true, "keep_running_in_background");
        KeepRunningInBackground.SettingChanged += (_, _) =>
        {
            Application.runInBackground = KeepRunningInBackground.Value;
        };

        MuteWhenUnfocused = LocalizedConfig.Bind(Config, ApplicationSection, "Mute When Unfocused", true, "mute_when_unfocused");
        MuteWhenUnfocused.SettingChanged += (_, _) =>
        {
            AudioListener.volume = MuteWhenUnfocused.Value && !Application.isFocused ? 0f : 1f;
        };

        SmoothPlayerMovement = LocalizedConfig.Bind(Config, MovementSection, "Smooth Player Movement", true, "smooth_player_movement");
        SmoothPlayerMovement.SettingChanged += (_, _) =>
        {
            Patches.PhysicsPatches.ApplyInterpolation();
        };

        MaxFootprints = LocalizedConfig.Bind(Config, FootprintsSection, "Max Footprints", 1000, "max_footprints", new AcceptableValueRange<int>(0, 10000));

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates");
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        OnFocusChanged(true);
    }

    private static void OnFocusChanged(bool focused)
    {
        Application.runInBackground = KeepRunningInBackground.Value;
        AudioListener.volume = MuteWhenUnfocused.Value && !focused ? 0f : 1f;
    }

    private static Sprite LoadEmbeddedSprite(string resourceName, string spriteName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return null;
        }

        var bytes = new byte[stream.Length];
        _ = stream.Read(bytes, 0, bytes.Length);

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        {
            name = spriteName,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        tex.LoadImage(bytes);

        var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 1f);
        sprite.name = spriteName;
        return sprite;
    }
}
