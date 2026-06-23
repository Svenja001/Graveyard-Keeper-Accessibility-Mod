namespace GraveChangesRedux;

[Harmony]
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string AdvancedSection = "── Advanced ──";
    private const string ChangesSection  = "── Changes ──";
    private const string UpdatesSection  = "── Updates ──";

    private const float MaxQualityValue = 30f;
    private static readonly SmartExpression MaxQualityExpression = SmartExpression.ParseExpression("30");

    private static readonly Dictionary<string,float> ItemDefBackups = new();
    private static readonly Dictionary<string,SmartExpression> ObjDefBackups = new();

    private static readonly string[] SkipThese = ["grave_empty", "_place", "place_", "grave_corp", "grave_exhume", "grave_ground"];
    private static TimestampedLogger Log { get; set; }
    private static ConfigEntry<bool> Debug { get; set; }
    internal static bool DebugEnabled;
    private static ConfigEntry<bool> ModifyGraves { get; set; }
    private static ConfigEntry<bool> ModifyObjects { get; set; }
    private static ConfigEntry<bool> IgnoreSkullLimit { get; set; }
    internal static bool IgnoreSkullLimitEnabled;
    internal static ConfigEntry<bool> CheckForUpdates { get; private set; }

    private void Awake()
    {
        Log = new TimestampedLogger(Logger);
        LogHelper.Log = Log;
        Lang.Init(Assembly.GetExecutingAssembly(), Log);

        Debug = LocalizedConfig.Bind(Config, AdvancedSection, "Debug Logging", false, "debug_logging");
        DebugEnabled = Debug.Value;
        Debug.SettingChanged += (_, _) => DebugEnabled = Debug.Value;

        DebugWarningDialog.Register(MyPluginInfo.PLUGIN_NAME, () => DebugEnabled);

        ModifyGraves = LocalizedConfig.Bind(Config, ChangesSection, "Modify Graves", true, "modify_graves", order: 2);
        ModifyGraves.SettingChanged += (_, _) => GameBalanceLoad();

        ModifyObjects = LocalizedConfig.Bind(Config, ChangesSection, "Modify Decorations", false, "modify_decorations", order: 1);
        ModifyObjects.SettingChanged += (_, _) => GameBalanceLoad();

        IgnoreSkullLimit = LocalizedConfig.Bind(Config, ChangesSection, "Ignore Body Skull Limit", true, "ignore_skull_limit", order: 3);
        IgnoreSkullLimitEnabled = IgnoreSkullLimit.Value;
        IgnoreSkullLimit.SettingChanged += (_, _) => IgnoreSkullLimitEnabled = IgnoreSkullLimit.Value;

        CheckForUpdates = LocalizedConfig.Bind(Config, UpdatesSection, "Check for Updates", true, "check_for_updates");

        UpdateChecker.Register(Info, CheckForUpdates);
        SettingsChangeLogger.Register(Config, Log);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameBalance), nameof(GameBalance.LoadGameBalance))]
    public static void GameBalanceLoad()
    {
        foreach (var itemDef in GameBalance.me.items_data.Where(itemDef => itemDef.id.StartsWith("grave", StringComparison.OrdinalIgnoreCase) && itemDef.quality_type is not ItemDefinition.QualityType.Stars))
        {
            if (SkipThese.Any(a => itemDef.id.Contains(a)))
            {
                if (DebugEnabled)
                {
                    Log.LogInfo($"ITEM: Skipping {itemDef.id} - {itemDef.quality}");
                }
                continue;
            }

            TryAdd(ItemDefBackups, itemDef.id, itemDef.quality);

            if (ModifyGraves.Value)
            {
                itemDef.quality = MaxQualityValue;
                if (DebugEnabled)
                {
                    Log.LogInfo($"ITEM: Set quality of {itemDef.id} to {MaxQualityValue}");
                }
            }
            else
            {
                itemDef.quality = ItemDefBackups[itemDef.id];
            }
        }

        foreach (var objDef in GameBalance.me.objs_data.Where(a => a.quality_type == ObjectDefinition.QualityType.Grave))
        {
            if (SkipThese.Any(a => objDef.id.Contains(a)))
            {
                if (DebugEnabled)
                {
                    Log.LogInfo($"OBJECT: Skipping {objDef.id} - {objDef.quality.GetRawExpressionString()}");
                }
                continue;
            }

            TryAdd(ObjDefBackups, objDef.id, objDef.quality);

            if (ModifyObjects.Value)
            {
                objDef.quality = MaxQualityExpression;
                if (DebugEnabled)
                {
                    Log.LogInfo($"OBJECT: Set quality of {objDef.id} to {MaxQualityValue}");
                }
            }
            else
            {
                objDef.quality = ObjDefBackups[objDef.id];
            }
        }
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.quality), MethodType.Getter)]
    public static IEnumerable<CodeInstruction> QualityTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var mathfMin = AccessTools.Method(typeof(Mathf), nameof(Mathf.Min), [typeof(float), typeof(float)]);
        var replacement = AccessTools.Method(typeof(Plugin), nameof(GraveQualityCap));
        foreach (var ins in instructions)
        {
            if (ins.Calls(mathfMin))
            {
                yield return new CodeInstruction(OpCodes.Call, replacement);
            }
            else
            {
                yield return ins;
            }
        }
    }

    // computed = floored decoration quality minus the body's red skulls.
    // skullCap = the body's white-skull count. With the option on, return the
    // decoration quality in full instead of clamping it to the white skulls.
    public static float GraveQualityCap(float computed, float skullCap)
    {
        return IgnoreSkullLimitEnabled ? computed : Mathf.Min(computed, skullCap);
    }

    private static bool TryAdd<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
    {
        if (dictionary.ContainsKey(key)) return false;
        dictionary.Add(key, value);
        return true;
    }
}
