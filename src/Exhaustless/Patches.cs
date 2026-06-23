namespace Exhaustless;

[Harmony]
// Run after known external mods that also write the TrySpendEnergy ref param, so this
// mod's percent/zero-out has the final say.
[HarmonyAfter("codesprint.energy_edit")]
public static class Patches
{
    private static readonly ItemDefinition.ItemType[] ToolItems =
    [
        ItemDefinition.ItemType.Axe, ItemDefinition.ItemType.Shovel, ItemDefinition.ItemType.Hammer,
        ItemDefinition.ItemType.Pickaxe, ItemDefinition.ItemType.FishingRod, ItemDefinition.ItemType.BodyArmor,
        ItemDefinition.ItemType.HeadArmor, ItemDefinition.ItemType.Sword
    ];

    // Sleep Anytime: reset each time a sleep starts; dawn flips _dawnSeen so the auto-wake can fire.
    private static bool _dawnSeen;
    private static float _prevTimeK;


    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.TrySpendPlayerGratitudePoints))]
    public static void CraftComponent_TrySpendPlayerGratitudePoints(ref float value)
    {
        if (Plugin.UnlimitedGratitude.Value)
        {
            value = 0;
            return;
        }

        if (Plugin.SpendHalfGratitude.Value) value /= 2f;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameBalance), nameof(GameBalance.LoadGameBalance))]
    public static void GameBalance_LoadGameBalance()
    {
        if (!Plugin.MakeToolsLastLonger.Value) return;

        var itemsToUpdate = GameBalance.me.items_data
            .Where(itemDef => ToolItems.Contains(itemDef.type) && itemDef.durability_decrease_on_use);

        foreach (var itemDef in itemsToUpdate)
        {
            itemDef.durability_decrease_on_use_speed = 0.005f;
        }
    }


    [HarmonyPrefix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.OnEquippedToolBroken))]
    public static void MainGame_OnEquippedToolBroken()
    {
        if (!Plugin.AutoEquipNewTool.Value) return;

        var equippedTool = MainGame.me.player.GetEquippedTool();
        var save = MainGame.me.save;
        var playerInv = save.GetSavedPlayerInventory();

        var matchingItems = playerInv.inventory
            .Where(item => item.definition.type == equippedTool.definition.type &&
                           item.durability_state is Item.DurabilityState.Full or Item.DurabilityState.Used);

        var replacement = matchingItems.FirstOrDefault();
        if (replacement == null) return;

        MainGame.me.player.EquipItem(replacement, -1, playerInv.is_bag ? playerInv : null);
        Lang.Reload();
        MainGame.me.player.Say(
            $"{Lang.Get("LuckyHadAnotherPartOne")} {replacement.definition.GetItemName()} {Lang.Get("LuckyHadAnotherPartTwo")}",
            null, false,
            SpeechBubbleGUI.SpeechBubbleType.Think,
            SmartSpeechEngine.VoiceID.None, true);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerComponent), nameof(PlayerComponent.Update))]
    public static void PlayerComponent_Update(PlayerComponent __instance)
    {
        if (!MainGame.game_started || !MainGame.me.player || !__instance) return;
        
        var maxEnergy = MainGame.me.save.max_energy;
        var maxHealth = MainGame.me.save.max_hp;
        var maxSanity = MainGame.me.save.max_sanity;

        if (Plugin.UnlimitedHealth.Value)
        {
            MainGame.me.player.hp = maxHealth;
        }

        if (Plugin.UnlimitedEnergy.Value)
        {
            MainGame.me.player.energy = maxEnergy;
        }

        if (Plugin.UnlimitedSanity.Value)
        {
            MainGame.me.player.sanity = maxSanity;
        }
    }


    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerComponent), nameof(PlayerComponent.SpendSanity))]
    public static void PlayerComponent_SpendSanity(ref float need_sanity)
    {
        if (Plugin.UnlimitedSanity.Value)
        {
            need_sanity = 0;
            return;
        }

        if (Plugin.SpendHalfSanity.Value) need_sanity /= 2f;
    }


    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerComponent), nameof(PlayerComponent.TrySpendEnergy))]
    public static void PlayerComponent_TrySpendEnergy(ref float need_energy)
    {
        if (Plugin.UnlimitedEnergy.Value)
        {
            need_energy = 0;
            return;
        }

        if (Plugin.SpendHalfEnergy.Value) need_energy /= 2f;
    }


    [HarmonyPrefix]
    [HarmonyPatch(typeof(SleepGUI), nameof(SleepGUI.Update))]
    public static void SleepGUI_Update(SleepGUI __instance)
    {
        if (__instance._state == SleepGUI.State.Sleeping && TimeOfDay.me)
        {
            var timeK = TimeOfDay.me.GetTimeK();
            if (_prevTimeK < TimeOfDay.MORNING && timeK >= TimeOfDay.MORNING)
            {
                _dawnSeen = true;
            }

            _prevTimeK = timeK;
        }

        if (!Plugin.SpeedUpSleep.Value) return;

        RestoreVitamins();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SleepGUI), nameof(SleepGUI.Open))]
    public static void SleepGUI_Open_Postfix()
    {
        if (!TimeOfDay.me) return;

        // Going to bed during the morning keeps the vanilla wake-up instead of sleeping a whole extra day.
        _dawnSeen = TimeOfDay.me.time_of_day_enum == TimeOfDay.TimeOfDayEnum.Morning;
        _prevTimeK = TimeOfDay.me.GetTimeK();
    }

    public static bool GateTooRested(bool tooRested)
    {
        return !Plugin.SleepAnytime.Value && tooRested;
    }

    public static bool StatsFullForWake(bool statsFull)
    {
        return statsFull && (!Plugin.SleepAnytime.Value || _dawnSeen);
    }

    // The bed refuses a fully rested player. Force the check false when Sleep Anytime is on;
    // false short-circuits the && so the "too rested" branch is never taken.
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(SleepGUI), nameof(SleepGUI.Open))]
    private static IEnumerable<CodeInstruction> SleepGUI_Open_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var equalsTo = AccessTools.Method(typeof(ExtentionTools), nameof(ExtentionTools.EqualsTo),
            [typeof(float), typeof(float), typeof(float)]);
        var inserted = false;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (!inserted && instruction.Calls(equalsTo))
            {
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patches), nameof(GateTooRested)));
                inserted = true;
            }
        }
    }

    // Sleeping normally ends the moment energy and health are full. While Sleep Anytime is on,
    // hold that auto-wake until morning. Waking manually with the interact key is untouched.
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(SleepGUI), nameof(SleepGUI.Update))]
    private static IEnumerable<CodeInstruction> SleepGUI_Update_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var equalsOrMore = AccessTools.Method(typeof(ExtentionTools), nameof(ExtentionTools.EqualsOrMore));
        var calls = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            // the second call is the health check that decides the auto-wake
            if (instruction.Calls(equalsOrMore) && ++calls == 2)
            {
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patches), nameof(StatsFullForWake)));
            }
        }
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(WaitingGUI), nameof(WaitingGUI.Update))]
    public static void WaitingGUI_Update_Postfix(WaitingGUI __instance)
    {
        if (!Plugin.AutoWakeFromMeditationWhenStatsFull.Value)
        {
            return;
        }

        var gameSave = MainGame.me.save;
        if (MainGame.me.player.energy.EqualsOrMore(gameSave.max_energy) &&
            MainGame.me.player.hp.EqualsOrMore(gameSave.max_hp))
        {
            __instance.StopWaiting();
        }
    }


    [HarmonyPrefix]
    [HarmonyPatch(typeof(WaitingGUI), nameof(WaitingGUI.Update))]
    public static void WaitingGUI_Update_Prefix()
    {
        if (!Plugin.SpeedUpMeditation.Value)
        {
            return;
        }

        RestoreVitamins();
    }
    private static void RestoreVitamins()
    {
        var maxEnergy = MainGame.me.save.max_energy;
        var maxHealth = MainGame.me.save.max_hp;
        
        MainGame.me.player.energy += 1.5f * Time.deltaTime;
        MainGame.me.player.hp += 1.5f * Time.deltaTime;

        if (MainGame.me.player.energy > maxEnergy)
        {
            MainGame.me.player.energy = maxEnergy;
        }

        if (MainGame.me.player.hp > maxHealth)
        {
            MainGame.me.player.hp = maxHealth;
        }
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.EquipItem))]
    public static void WorldGameObject_EquipItem_Postfix(Item item)
    {
        if (!Plugin.MakeToolsLastLonger.Value)
        {
            return;
        }

        if (!ToolItems.Contains(item.definition.type))
        {
            return;
        }

        if (item.definition.durability_decrease_on_use)
        {
            item.definition.durability_decrease_on_use_speed = 0.005f;
        }
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.GetParam))]
    private static void WorldGameObject_GetParam_Postfix(WorldGameObject __instance, string param_name,
        ref float __result)
    {
        if (!__instance._is_player || !param_name.Contains("tiredness"))
        {
            return;
        }

        var tiredness = __instance._data.GetParam("tiredness");
        var newTirednessLimit = (float) Plugin.EnergySpendBeforeSleepDebuff.Value;
        __result = tiredness < newTirednessLimit ? 250 : 350;
    }
}