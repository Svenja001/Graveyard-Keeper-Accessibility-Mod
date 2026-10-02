namespace KeepersCandles;

[Harmony]
public static class Patches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.Update))]
    public static void MainGame_Update()
    {
        if (!Plugin.CanFindCandles()) return;

        TryExtinguish(
            "candle",
            Plugin.ExtinguishCandleKeyBind.Value,
            Plugin.ExtinguishCandleControllerButton.Value,
            Plugin.GetCandles,
            "TooFar",
            "NoneFound");

        TryExtinguish(
            "incense",
            Plugin.ExtinguishIncenseKeyBind.Value,
            Plugin.ExtinguishIncenseControllerButton.Value,
            Plugin.GetIncenses,
            "TooFarIncense",
            "NoneFoundIncense");
    }

    private static void TryExtinguish(string typeLabel, KeyboardShortcut keybind, string controllerButtonName,
        Func<List<WorldGameObject>> getBurners, string tooFarLangKey, string noneFoundLangKey)
    {
        var keyUp = keybind.MainKey != KeyCode.None && keybind.IsUp();
        var gamepadDown = LazyInput.gamepad_active
                          && !string.IsNullOrEmpty(controllerButtonName)
                          && controllerButtonName != nameof(GamePadButton.None)
                          && ReInput.players.GetPlayer(0).GetButtonDown(controllerButtonName);
        if (!gamepadDown && !keyUp) return;

        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[Extinguish:{typeLabel}] trigger: key={keyUp} gamepad={gamepadDown} zone={MainGame.me.player.GetMyWorldZoneId()}");
        }

        WorldGameObject closest = null;
        var closestDistance = float.MaxValue;
        var count = 0;

        foreach (var burner in getBurners())
        {
            count++;
            var distance = Vector3.Distance(burner.grid_pos, Plugin.PlayerPosition);

            if (!(distance < closestDistance)) continue;

            closestDistance = distance;
            closest = burner;
        }

        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[Extinguish:{typeLabel}] scanned {count} lit {typeLabel}(s) in zone; closest={(closest ? closest.obj_id : "none")} distance={closestDistance:F2} limit={Plugin.ExtinguishDistance.Value}");
        }

        if (closest)
        {
            if (closestDistance <= Plugin.ExtinguishDistance.Value)
            {
                var unlit = Plugin.GetUnlitReplacement(closest);
                if (unlit.IsNullOrWhiteSpace())
                {
                    Helpers.Log($"Could not find unlit {typeLabel} for {closest.obj_id}. Last craft ID: {closest.components.craft.last_craft_id}. Please report this!", true);
                    ResetArrow();
                    return;
                }
                if (Plugin.DebugEnabled)
                {
                    Helpers.Log($"[Extinguish:{typeLabel}] extinguishing {closest.obj_id} → {unlit} (last_craft_id={closest.components.craft.last_craft_id})");
                }
                ResetArrow();
                ReplaceAndDrop(closest, unlit);
            }
            else
            {
                if (Plugin.DebugEnabled)
                {
                    Helpers.Log($"[Extinguish:{typeLabel}] too far - pointing arrow at {closest.obj_id} ({closestDistance:F2} > {Plugin.ExtinguishDistance.Value})");
                }
                SetArrow(closest);
                MainGame.me.player.Say(Lang.Get(tooFarLangKey), null, false, SpeechBubbleGUI.SpeechBubbleType.Think, SmartSpeechEngine.VoiceID.None, true);
            }
        }
        else
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Extinguish:{typeLabel}] no lit {typeLabel}s in current zone");
            }
            ResetArrow();
            MainGame.me.player.Say(Lang.Get(noneFoundLangKey), null, false, SpeechBubbleGUI.SpeechBubbleType.Think, SmartSpeechEngine.VoiceID.None, true);
        }
    }

    internal static void ChurchColumnsToggle()
    {
        var toggled = 0;
        foreach (var column in Plugin.ChurchColumnsList.ToList().Where(column => column))
        {
            column.gameObject.SetActive(Plugin.ChurchColumns.Value);
            toggled++;
        }

        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[ChurchColumns] visibility={Plugin.ChurchColumns.Value}; toggled {toggled}/{Plugin.ChurchColumnsList.Count} column(s)");
        }
    }

    internal static void ResetArrow()
    {
        GUIElements.me.tutorial_arrow.SetActive(false);
        GUIElements.me.tutorial_arrow._attached_wgo = null;
        GUIElements.me.tutorial_arrow._visible = false;
    }

    private static void SetArrow(WorldGameObject wgo)
    {
        if (!Plugin.DirectionalArrow.Value)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log("[SetArrow] skipped - DirectionalArrow disabled");
            }
            ResetArrow();
            return;
        }

        GUIElements.me.tutorial_arrow.Init();
        GUIElements.me.tutorial_arrow.AttachToWGO(wgo);
        GUIElements.me.tutorial_arrow._visible = true;
    }

    internal static void OnGameBalanceLoaded()
    {
        try
        {
            var craftUpdates = 0;
            // Visit every candle and incense definition, including the ones set to burn down, so
            // switching burn-down on puts the game's own values back.
            foreach (var obj in GameBalance._instance.craft_data.Where(obj => Plugin.ShouldProcess(obj.id)))
            {
                BalanceSnapshots.Apply(obj, Plugin.KeepsBurning(obj.id));
                craftUpdates++;
            }

            var defUpdates = 0;
            foreach (var obj in GameBalance._instance.objs_data.Where(obj => Plugin.ShouldProcess(obj.id)))
            {
                BalanceSnapshots.Apply(obj, Plugin.KeepsBurning(obj.id));
                defUpdates++;
            }

            var wgoUpdates = 0;
            foreach (var wgo in WorldMap._objs.Where(wgo => Plugin.ShouldProcess(wgo.obj_id) || Plugin.ShouldProcess(wgo.obj_def.id)))
            {
                BalanceSnapshots.Apply(wgo.obj_def, Plugin.KeepsBurning(wgo.obj_id) || Plugin.KeepsBurning(wgo.obj_def.id));
                wgoUpdates++;
            }

            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[OnGameBalanceLoaded] candelabrum/incense updates - crafts={craftUpdates}, obj_defs={defUpdates}, live wgos={wgoUpdates}");
            }

            FixCandles();
            ClearStrandedIncense();
            ChurchColumnsToggle();
        }
        catch (Exception ex)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[OnGameBalanceLoaded] swallowed exception (expected during early scene loads): {ex.Message}");
            }
        }
    }

    private static void ReplaceAndDrop(WorldGameObject wgo, string unlitCandle)
    {
        var litId = wgo.obj_id;
        var startOutputs = wgo.components.craft.current_craft?.output_to_wgo_on_start;

        // With burn-down on, giving the candle or incense back would let a player put it out
        // just before it finishes and relight it, so it never burns down.
        var burnsDown = !Plugin.KeepsBurning(litId);
        List<Item> refund = burnsDown ? [] : GetRefund(wgo, litId, unlitCandle);

        wgo.ReplaceWithObject(unlitCandle, true);
        wgo.components.craft.is_crafting = false;

        // Lighting incense puts a hidden church rating item inside the burner. The game takes it
        // back out when a craft ends, but ours never ends, so remove it here.
        if (startOutputs is { Count: > 0 })
        {
            wgo.data.RemoveItems(startOutputs);
        }

        var cleared = ClearStrandedRatingItems(wgo);
        if (cleared > 0 && Plugin.DebugEnabled)
        {
            Helpers.Log($"[ReplaceAndDrop] removed {cleared} leftover church rating item(s) from '{wgo.obj_id}'");
        }

        if (burnsDown)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[ReplaceAndDrop] no refund for '{litId}' - burn-down is on for this type");
            }
            return;
        }

        if (refund.Count == 0)
        {
            Helpers.Log($"Could not find candle item used for {litId}. Please report this!", true);
            return;
        }

        foreach (var item in refund)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[ReplaceAndDrop] dropping recovered candle item={item.id} qty={item.value} at {wgo.tf.position}");
            }
            DropResGameObject.Drop(wgo.tf.position, item, wgo.tf.parent, Direction.ToPlayer, 3f, Random.Range(0, 2), force_stacked_drop: true);
        }
    }

    // What to hand back when extinguishing. Candle holders start their burn-out craft the moment
    // they're lit, which wipes the list of candles used, so read the candles off the recipe that
    // lit the holder instead. The drop keeps the item it's given, so always hand it a copy.
    private static List<Item> GetRefund(WorldGameObject wgo, string litId, string unlitId)
    {
        if (!Plugin.MatchesKeyword(litId, Plugin.Candelabrum))
        {
            var used = wgo.components.craft._cur_craft_items_used.FirstOrDefault();
            return used == null ? [] : [new Item(used)];
        }

        // wall_candelabrum_3_3 was lit by wall_candelabrum_3_to_3_3.
        var marker = Plugin.Candelabrum + "_";
        var at = litId.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return [];

        var craftId = $"{unlitId}_to_{litId.Substring(at + marker.Length)}";
        var craft = GameBalance.me.GetDataOrNull<CraftDefinition>(craftId);
        if (craft?.needs == null) return [];

        return craft.needs.Where(need => need != null && need.value > 0).Select(need => new Item(need)).ToList();
    }

    private static void FixCandles()
    {
        var fixedCount = 0;
        foreach (var wgo in WorldMap._objs.Where(wgo => Plugin.IsLit(wgo.obj_id) && Plugin.KeepsBurning(wgo.obj_id)))
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[FixCandles] correcting '{wgo.obj_id}' crafting status → true");
            }
            wgo.components.craft.is_crafting = true;
            fixedCount++;
        }

        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[FixCandles] corrected {fixedCount} burner(s)");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ChunkedGameObject), nameof(ChunkedGameObject.Init))]
    public static void ChunkedGameObject_Init(ChunkedGameObject __instance)
    {
        var name = __instance.name;
        var path = Plugin.GetPath(__instance.transform);
        if (name.Contains(Plugin.Column) && path.Contains(Plugin.Church))
        {
            Plugin.ChurchColumnsList.Add(__instance.gameObject);
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[ChunkedGameObject.Init] registered church column '{name}' (total={Plugin.ChurchColumnsList.Count})");
            }

            ChurchColumnsToggle();
        }
    }

    // Empty incense burners. Their balance data gives them no inventory, so any rating item
    // found in one was left behind by an earlier extinguish.
    private static readonly string[] UnlitIncenseIds = ["c_obj_incense_1_place", "c_obj_incense_2_place"];
    private const string RatingItemPrefix = "pseudo_item_qual_";

    private static int ClearStrandedRatingItems(WorldGameObject wgo)
    {
        if (!wgo || !UnlitIncenseIds.Contains(wgo.obj_id)) return 0;

        var inventory = wgo.data?.inventory;
        if (inventory == null) return 0;

        return inventory.RemoveAll(item => item?.id != null && item.id.StartsWith(RatingItemPrefix));
    }

    // Fixes saves where extinguishing incense on an older version already left extra rating behind.
    private static void ClearStrandedIncense()
    {
        var removed = 0;
        foreach (var wgo in WorldMap._objs)
        {
            removed += ClearStrandedRatingItems(wgo);
        }

        if (removed > 0 && Plugin.DebugEnabled)
        {
            Helpers.Log($"[ClearStrandedIncense] removed {removed} leftover church rating item(s) from empty incense burners");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameBalance), nameof(GameBalance.LoadGameBalance))]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.GlobalEventsCheck))]
    [HarmonyPatch(typeof(ChunkManager), nameof(ChunkManager.RescanAllObjects))]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.LateSaveFixer))]
    public static void OnGameBalanceLoaded_Postfix()
    {
        OnGameBalanceLoaded();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.ReallyUpdateComponent))]
    public static bool CraftComponent_ReallyUpdateComponent(CraftComponent __instance)
    {
        var blocked = Plugin.KeepsBurning(__instance.wgo.obj_id);
        if (blocked && Plugin.DebugEnabled)
        {
            Helpers.Log($"[CraftComponent.ReallyUpdateComponent] skipping craft tick for '{__instance.wgo.obj_id}' (keeps candle/incense lit indefinitely)");
        }
        return !blocked;
    }
}
