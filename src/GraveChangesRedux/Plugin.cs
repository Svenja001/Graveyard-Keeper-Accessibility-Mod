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

    // The bishop gives this quest at the end of your first talk with him. It completes when you
    // collect your tools.
    private const string FirstTalkQuest = "take_tools_from_grave_chest";

    // With a high rating the bishop's first task can be handed in during that first talk, which
    // cuts the talk short. So nothing here applies until the save shows that talk is over and
    // the tools have been collected.
    private static bool _firstTalkDone;
    private static bool _noticePending;

    private static bool FirstTalkDone()
    {
        var quests = MainGame.me?.save?.quests;
        if (quests == null) return false;
        if (quests.IsQuestSucced(FirstTalkQuest)) return true;

        // First task already handed in: there is nothing left to hold back for, and a save that
        // handed it in during the first talk would otherwise never get the changes.
        return MainGame.me.player?.GetParam("cup_20_reached") >= 1f;
    }

    // Older versions let the bishop's first task be handed in during the first talk. That skipped
    // the step that gives this quest, and without it he only ever tells you to fetch your tools.
    // Give the quest now; the game clears his line once the tools are collected.
    private static void RepairCutShortFirstTalk()
    {
        var player = MainGame.me?.player;
        var quests = MainGame.me?.save?.quests;
        if (player == null || quests == null) return;
        if (player.GetParam("cup_20_reached") < 1f || player.GetParam(FirstTalkQuest) < 1f) return;
        if (quests.CheckIfQuestWasExecuted(FirstTalkQuest) || quests.IsQuestCurrent(FirstTalkQuest)) return;

        var quest = GameBalance.me.GetDataOrNull<QuestDefinition>(FirstTalkQuest);
        if (quest == null) return;

        quests.StartQuest(quest);
        quests.CheckQuestsState();
        Log.LogInfo("This save had the bishop's first talk cut short. Started his tools quest so his dialogue can move on.");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.OnGameStartedPlaying))]
    public static void MainGame_OnGameStartedPlaying()
    {
        RepairCutShortFirstTalk();
        GameBalanceLoad();
        _noticePending = !_firstTalkDone && (ModifyGraves.Value || ModifyObjects.Value || IgnoreSkullLimit.Value);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(QuestSystem), nameof(QuestSystem.OnQuestSucceed))]
    public static void QuestSystem_OnQuestSucceed(QuestState q_to_end)
    {
        if (_firstTalkDone || q_to_end?.definition?.id != FirstTalkQuest) return;
        _noticePending = false;
        GameBalanceLoad();
    }

    // Tell the player why nothing has changed yet. Waits until they have control and no window
    // is open.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.Update))]
    public static void MainGame_Update()
    {
        if (!_noticePending) return;
        if (!MainGame.game_started || MainGame.paused || !BaseGUI.all_guis_closed) return;
        if (MainGame.me?.player?.components?.character?.control_enabled != true) return;

        _noticePending = false;
        GUIElements.me.dialog.OpenOK(MyPluginInfo.PLUGIN_NAME, null, Lang.Get("FirstTalkNotice"), true, string.Empty);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameBalance), nameof(GameBalance.LoadGameBalance))]
    public static void GameBalanceLoad()
    {
        _firstTalkDone = FirstTalkDone();

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

            if (ModifyGraves.Value && _firstTalkDone)
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

            if (ModifyObjects.Value && _firstTalkDone)
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
        return IgnoreSkullLimitEnabled && _firstTalkDone ? computed : Mathf.Min(computed, skullCap);
    }

    private static bool TryAdd<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
    {
        if (dictionary.ContainsKey(key)) return false;
        dictionary.Add(key, value);
        return true;
    }
}
