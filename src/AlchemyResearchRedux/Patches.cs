namespace AlchemyResearchRedux;

[Harmony]
public static class Patches
{
    private const string AlchemyWorkbench1ObjID = "mf_alchemy_craft_02";
    private const string AlchemyWorkbench2ObjID = "mf_alchemy_craft_03";
    private const string IngredientContainerResult = "ingredient container result";
    private const string IngredientCell1 = "ingredients/ingredient container/Base Item Cell";
    private const string IngredientCell2 = "ingredients/ingredient container (1)/Base Item Cell";
    private const string IngredientCell3 = "ingredients/ingredient container (2)/Base Item Cell";


    private static string GetLocalResult()
    {
        var lang = GameSettings._cur_lng;
        return lang switch
        {
            "en" => "Result",
            "fr" => "Résultat",
            "de" => "Ergebnis",
            "zh_cn" => "结果",
            "zh-cn" => "结果",
            "es" => "Resultado",
            "pt-br" => "Resultado",
            "pt_br" => "Resultado",
            "ko" => "결과",
            "ja" => "結果",
            "ru" => "Результат",
            "it" => "Risultato",
            "pl" => "Wynik",
            _ => "Result"
        };
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MixedCraftGUI), nameof(MixedCraftGUI.OnResourcePickerClosed))]
    public static void MixedCraftGUI_OnResourcePickerClosed(MixedCraftGUI __instance, Item item)
    {
        var objId = __instance.GetCrafteryWGO().obj_id;
        var crafteryTransform = GetCrafteryTransform(__instance.transform, objId);
        var resultTransform = __instance.transform.Find(IngredientContainerResult);

        if (!crafteryTransform)
        {
            ResultPreviewDrawUnknown(__instance.transform.Find(IngredientContainerResult));
            return;
        }

        var ingredient1 = crafteryTransform.Find(IngredientCell1)?.GetComponent<BaseItemCellGUI>();
        var ingredient2 = crafteryTransform.Find(IngredientCell2)?.GetComponent<BaseItemCellGUI>();
        var ingredient3 = crafteryTransform.Find(IngredientCell3)?.GetComponent<BaseItemCellGUI>();

        var ingred1 = ingredient1?.item?.id ?? string.Empty;
        var ingred2 = ingredient2?.item?.id ?? string.Empty;
        var ingred3 = ingredient3?.item?.id ?? string.Empty;

        var craftId = $"mix:{objId}:{ingred1}:{ingred2}:{ingred3}:";

        var resultId = AlchemyRecipe.GetRecipeResult(craftId)?.Result ?? string.Empty;

        if (resultId.IsNullOrWhiteSpace()) return;

        var itemDef = GameBalance.me.GetData<ItemDefinition>(resultId);

        if (itemDef == null)
        {
            Plugin.Log.LogWarning($"No item definition found: {resultId}");
            return;
        }

        ResultPreviewDrawItem(resultTransform, itemDef.id);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MixedCraftGUI), nameof(MixedCraftGUI.OpenAsAlchemy))]
    public static void PatchOpenAsAlchemy(MixedCraftGUI __instance, WorldGameObject craftery_wgo, string preset_name)
    {
        var crafteryTransform = GetCrafteryTransform(__instance.transform, craftery_wgo.obj_id);

        if (!crafteryTransform || __instance.transform.Find(IngredientContainerResult)) return;

        foreach (var craft in __instance.crafts)
        {
            var craftDef = GameBalance.me.GetData<CraftDefinition>(craft.id);
            var output = craftDef.GetFirstRealOutput();
            if (output.id.StartsWith("goo")) continue;

            var split = craft.id.Split(':');

            var ingredient1 = split[2];
            var ingredient2 = split[3];
            var ingredient3 = split[4];

            var recipe = new AlchemyRecipe
            {
                CraftString = craft.id,
                Ingredient1 = ingredient1,
                Ingredient2 = ingredient2,
                Ingredient3 = ingredient3,
                Result = output.id
            };

            AlchemyRecipe.AddRecipe(recipe);
        }


        var resultContainer = CreateResultContainer(crafteryTransform, __instance.transform, craftery_wgo.obj_id);

        ResultPreviewDrawUnknown(resultContainer);
        CreateResultLabel(resultContainer);

        if (Plugin.AutoRefillOnOpen.Value)
        {
            ReapplyLastMix(__instance);
        }
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(MixedCraftGUI), nameof(MixedCraftGUI.Hide))]
    public static void MixedCraftGUI_Hide(MixedCraftGUI __instance)
    {
        var resultTransform = __instance.transform.Find(IngredientContainerResult);
        if (resultTransform)
        {
            Object.Destroy(resultTransform.gameObject);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MixedCraftGUI), nameof(MixedCraftGUI.OnCraftPressed))]
    public static void MixedCraftGUI_OnCraftPressed(MixedCraftGUI __instance)
    {
        if (!__instance.IsCraftAllowed()) return;

        var objId = __instance.GetCrafteryWGO().obj_id;
        if (objId is not (AlchemyWorkbench1ObjID or AlchemyWorkbench2ObjID)) return;

        var preset = __instance._current_preset;
        var ingredients = preset.GetSelectedItems();

        // Remember what went in - including deliberate failure combos - so the player
        // can drop the same ingredients back later instead of re-picking each time.
        var ingredientIds = ingredients.Select(item => item.IsEmpty() ? string.Empty : item.id).ToList();
        LastMix.Set(objId, ingredientIds);

        if (Plugin.DebugEnabled)
        {
            Plugin.Log.LogInfo($"Remembered last mix for {objId}: [{string.Join(", ", ingredientIds)}]");
        }

        // The preview memory below only tracks real (exact-match) recipes.
        var craftDef = __instance.GetCraftDefinition(false, out _);
        if (craftDef == null || !craftDef.id.StartsWith("mix:mf_alchemy")) return;

        var result = craftDef.GetFirstRealOutput();

        var recipe = new AlchemyRecipe
        {
            CraftString = craftDef.id,
            Ingredient1 = ingredients.Count > 0 ? ingredients[0].id : string.Empty,
            Ingredient2 = ingredients.Count > 1 ? ingredients[1].id : string.Empty,
            Ingredient3 = ingredients.Count > 2 ? ingredients[2].id : string.Empty,
            Result = result.id
        };

        AlchemyRecipe.AddRecipe(recipe);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BaseGUI), nameof(BaseGUI.Update))]
    public static void BaseGUI_Update(BaseGUI __instance)
    {
        if (__instance is not MixedCraftGUI mixedCraftGui || !mixedCraftGui.is_shown_and_top) return;

        var keyDown = Plugin.RefillLastMixKey.Value.IsDown();
        var padDown = Plugin.RefillButton != GamePadButton.None
                      && LazyInput.gamepad_active
                      && LazyInput._gamepad != null
                      && LazyInput._gamepad._rewired_bindings.TryGetValue(Plugin.RefillButton, out var actionId)
                      && ReInput.players.GetPlayer(0).GetButtonDown(actionId);

        if (!keyDown && !padDown) return;

        ReapplyLastMix(mixedCraftGui);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIWidget), nameof(UIWidget.OnInit))]
    [HarmonyPatch(typeof(UIWidget), nameof(UIWidget.OnStart))]
    public static void UIWidget_Init(UIWidget __instance)
    {
        if (__instance.name.Contains("ingredient container") && !__instance.name.Contains("result"))
        {
            if (!__instance.transform.GetComponent<WidgetPos>())
            {
                __instance.transform.gameObject.AddComponent<WidgetPos>();
            }
        }
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlatformSpecific), nameof(PlatformSpecific.SaveGame))]
    public static void PlatformSpecific_SaveGame(SaveSlotData slot)
    {
        AlchemyRecipe.SaveRecipesToFile();
        LastMix.SaveToFile();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlatformSpecific), nameof(PlatformSpecific.LoadGame))]
    public static void PatchLoadGame(SaveSlotData slot, PlatformSpecific.OnGameLoadedDelegate on_lodaded)
    {
        AlchemyRecipe.LoadRecipesFromFile();
        LastMix.LoadFromFile();
    }


    private static void ReapplyLastMix(MixedCraftGUI gui)
    {
        var crafteryWgo = gui.GetCrafteryWGO();
        if (!crafteryWgo) return;

        var objId = crafteryWgo.obj_id;
        if (objId is not (AlchemyWorkbench1ObjID or AlchemyWorkbench2ObjID)) return;

        if (!LastMix.TryGet(objId, out var ingredientIds) || ingredientIds == null) return;

        var preset = gui._current_preset;
        var inventory = gui._multi_inventory;
        if (!preset || inventory == null) return;

        var cells = preset.items;
        var slots = Math.Min(ingredientIds.Count, cells.Length);

        for (var n = 0; n < slots; n++)
        {
            var id = ingredientIds[n];
            if (string.IsNullOrEmpty(id)) continue;

            var cell = cells[n];
            if (!cell) continue;

            // Already holding the right thing.
            if (!cell.item.IsEmpty() && cell.item.id == id) continue;

            var def = GameBalance.me.GetDataOrNull<ItemDefinition>(id);
            if (def == null) continue;

            // Universal ingredients fit any slot; the rest must match the slot type.
            if (def.alch_type != ItemDefinition.AlchemyType.Universal && def.alch_type != (ItemDefinition.AlchemyType)(n + 1)) continue;

            var item = new Item(id, 1) { equipped_as = ItemDefinition.EquipmentType.None };

            // Skip the slot if the player no longer has the ingredient.
            if (!inventory.RemoveItem(item)) continue;

            if (!cell.item.IsEmpty())
            {
                inventory.AddItem(cell.item);
            }

            cell.DrawItem(item);
        }

        gui._craft_button.SetEnabled(gui.IsCraftAllowed());
    }


    private static Transform GetCrafteryTransform(Transform craftingStation, string crafteryWgoObjectId)
    {
        return crafteryWgoObjectId switch
        {
            AlchemyWorkbench1ObjID => craftingStation.Find("alchemy_craft_02"),
            AlchemyWorkbench2ObjID => craftingStation.Find("alchemy_craft_03"),
            _ => null
        };
    }


    private static Transform CreateResultContainer(Transform crafteryTransform, Transform parentTransform, string objId)
    {
        var container1 = crafteryTransform.Find("ingredients/ingredient container (1)");

        var resultContainer = Object.Instantiate(container1.gameObject, parentTransform);
        resultContainer.name = IngredientContainerResult;
        resultContainer.transform.localPosition = objId == AlchemyWorkbench2ObjID
            ? new Vector3(container1.localPosition.x, -20f, 0f)
            : new Vector3(0f, -20f, 0f);

        return resultContainer.transform;
    }

    private static void CreateResultLabel(Transform resultContainer)
    {
        var baseCell = resultContainer.Find("Base Item Cell/x2 container/counter");
        var resultLabel = Object.Instantiate(baseCell.gameObject, resultContainer);
        resultLabel.name = "label result";

        var labelComponent = resultLabel.GetComponent<UILabel>();
        labelComponent.text = GetLocalResult();
        labelComponent.pivot = UIWidget.Pivot.Center;
        labelComponent.color = new Color(0.937f, 0.87f, 0.733f);
        labelComponent.overflowWidth = 0;
        labelComponent.overflowMethod = 0;
        labelComponent.topAnchor.target = resultContainer;
        labelComponent.bottomAnchor.target = resultContainer;
        labelComponent.rightAnchor.target = resultContainer;
        labelComponent.leftAnchor.target = resultContainer;
        labelComponent.leftAnchor.relative = -10f;
        labelComponent.rightAnchor.relative = 10f;
        labelComponent.topAnchor.relative = -9f;
        labelComponent.bottomAnchor.relative = -10f;
    }

    private static void ResultPreviewDrawItem(Transform resultPreview, string itemId)
    {
        var baseItemCellGui = resultPreview.GetComponentInChildren<BaseItemCellGUI>();
        baseItemCellGui.DrawEmpty();
        baseItemCellGui.DrawItem(itemId, 1);
    }

    private static void ResultPreviewDrawUnknown(Transform resultPreview)
    {
        var baseItemCellGui = resultPreview?.GetComponentInChildren<BaseItemCellGUI>();
        baseItemCellGui?.DrawEmpty();
        baseItemCellGui?.DrawUnknown();
    }
}